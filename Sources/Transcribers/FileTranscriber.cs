using NAudio.Wave;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.Ggml;
using WhisperCLI.Transcribers.Robust;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace WhisperCLI.Transcribers
{
    public class FileTranscriber
    {
        private readonly ILogger _logger;
        private readonly Func<GgmlType, CancellationToken, Task<FileInfo>>? _modelResolver;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        private string? _lastWorkerRuntime;
        private bool _cudaLibraryGuardLogged;
        private readonly HashSet<string> _dynamicHallucinationSegments = new(StringComparer.Ordinal);

        public FileTranscriber(
            ILogger logger,
            Func<GgmlType, CancellationToken, Task<FileInfo>>? modelResolver = null)
        {
            _logger = logger;
            _modelResolver = modelResolver;
        }

        /// <summary>
        /// Lightweight path retained for short microphone recordings.
        /// Long file transcription should use TranscribeRobustAsync.
        /// </summary>
        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, WhisperProcessor processor, CancellationToken token)
        {
            await CheckFfmpegAsync(token);
            FileInfo normalized = await ConvertToWaveFileAsync(inputFile, token);
            try
            {
                StringBuilder sb = new();
                Stopwatch sw = Stopwatch.StartNew();
                _logger.Information("Starting short transcription for {inputFile}", inputFile.Name);

                await using FileStream waves = normalized.OpenRead();
                await foreach (var result in processor.ProcessAsync(waves, token))
                {
                    AppendText(sb, result.Text);
                    _logger.Information("{lang}: {start}->{end}: {text}", result.Language,
                        result.Start.ToString(@"hh\:mm\:ss"), result.End.ToString(@"hh\:mm\:ss"), result.Text);
                }

                _logger.Information("Elapsed: {elapsed}", sw.Elapsed.ToString(@"hh\:mm\:ss"));
                string textFilePath = Path.ChangeExtension(inputFile.FullName, ".txt");
                await File.WriteAllTextAsync(textFilePath, sb.ToString(), Encoding.UTF8, token);
                _logger.Information("Transcription complete. Output saved to: {textFilePath}", textFilePath);
                return new FileInfo(textFilePath);
            }
            finally
            {
                TryDelete(normalized.FullName);
            }
        }

        public async Task<FileInfo> TranscribeRobustAsync(FileInfo inputFile, AppOptions options, CancellationToken token)
        {
            _dynamicHallucinationSegments.Clear();

            if (_modelResolver is null)
            {
                throw new InvalidOperationException("Robust transcription requires a model resolver.");
            }

            List<GgmlType> fallbackModels = ResolveFallbackModels(options.Model, options.FallbackModels);
            FileInfo? completedOutput = await TryReuseCompletedRunAsync(inputFile, options, fallbackModels, token);
            if (completedOutput is not null)
                return completedOutput;

            // Resolve the primary model before doing expensive audio preparation. Native Whisper
            // itself is intentionally loaded only inside an isolated worker process so a backend
            // crash cannot terminate the parent CLI or poison the checkpoint.
            ModelFileCache models = new(_modelResolver);
            _logger.Information("Pre-resolving primary Whisper model file before transcription: {model}", options.Model);
            await models.GetAsync(options.Model, token);
            _logger.Information("Primary Whisper model file is ready: {model}", options.Model);

            if (ShouldUseLinuxNvidiaCudaOnly())
            {
                CudaRuntimeLocator.LogAndValidate(_logger);
            }

            await CheckFfmpegAsync(token);
            FileInfo normalized = await ConvertToWaveFileAsync(inputFile, token);
            DateTime startedUtc = DateTime.UtcNow;

            try
            {
                TimeSpan audioDuration = WaveChunkReader.GetDuration(normalized.FullName);
                _logger.Information("Normalized audio duration: {duration}", FormatClock(audioDuration));

                List<SpeechRegion> speechRegions = [];
                bool usedVad = false;
                if (options.UseVad)
                {
                    try
                    {
                        speechRegions = DetectSpeechRegions(normalized, options, token);
                        usedVad = speechRegions.Count > 0;
                        if (usedVad)
                        {
                            _logger.Information("Managed silence detector found {count} active-audio regions for chunk-boundary hints", speechRegions.Count);
                        }
                        else
                        {
                            _logger.Warning("Managed silence detector found no reliable activity regions; using fixed-duration chunks so no audio can be skipped");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Managed silence detector failed; falling back to fixed-duration chunking");
                    }
                }

                TimeSpan targetChunk = TimeSpan.FromSeconds(options.ChunkSeconds);
                TimeSpan maxChunk = TimeSpan.FromSeconds(Math.Max(options.MaxChunkSeconds, options.ChunkSeconds));
                List<AudioChunk> chunks;
                if (usedVad)
                {
                    chunks = ChunkPlanner.FromVad(
                        speechRegions,
                        audioDuration,
                        targetChunk,
                        maxChunk,
                        TimeSpan.FromMilliseconds(options.VadEdgePaddingMs));
                }
                else
                {
                    chunks = ChunkPlanner.Fixed(audioDuration, targetChunk);
                }

                _logger.Information("Prepared {count} independent transcription chunks (target {target}s, hard max {max}s)",
                    chunks.Count, options.ChunkSeconds, options.MaxChunkSeconds);

                string fingerprint = BuildFingerprint(options, fallbackModels, usedVad);
                string checkpointPath = BuildModelScopedPath(inputFile, options.Model, ".transcription.checkpoint.json");
                string legacyCheckpointPath = BuildSidecarPath(inputFile, ".transcription.checkpoint.json");
                TranscriptionCheckpoint checkpoint = options.Resume
                    ? await LoadCheckpointAsync(
                        checkpointPath,
                        legacyCheckpointPath,
                        inputFile,
                        fingerprint,
                        options,
                        token)
                    : NewCheckpoint(inputFile, fingerprint);

                if (options.Resume && checkpoint.CompletedChunks.Count > 0)
                {
                    (List<AudioChunk> ResumedPlan, TimeSpan PreservedUntil) resume = PreserveCheckpointPrefix(
                        chunks, checkpoint.CompletedChunks, audioDuration, speechRegions);
                    chunks = resume.ResumedPlan;
                    if (resume.PreservedUntil > TimeSpan.Zero)
                    {
                        _logger.Information(
                            "Preserved completed checkpoint prefix through {resumeAt}; only the remaining audio was replanned",
                            FormatClock(resume.PreservedUntil));
                    }
                    _logger.Information(
                        "Active transcription plan after checkpoint merge: {count} chunks total",
                        chunks.Count);
                }

                string partialTextPath = BuildModelScopedPath(inputFile, options.Model, ".partial.txt");
                string partialSrtPath = BuildModelScopedPath(inputFile, options.Model, ".partial.srt");
                string partialVttPath = BuildModelScopedPath(inputFile, options.Model, ".partial.vtt");
                _logger.Information("Incremental transcript: {partialTextPath}", partialTextPath);
                _logger.Information("Incremental subtitles: {partialSrtPath} and {partialVttPath}", partialSrtPath, partialVttPath);
                _logger.Information("Resume checkpoint: {checkpointPath}", checkpointPath);

                if (checkpoint.CompletedChunks.Count > 0)
                {
                    await WriteProgressOutputsAsync(inputFile, options.Model, checkpoint.CompletedChunks, chunks.Count, CancellationToken.None);
                }
                else
                {
                    // Do not leave stale partial/review files from an incompatible or failed older run
                    // looking like current progress while the first isolated worker is running.
                    TryDelete(partialTextPath);
                    TryDelete(partialSrtPath);
                    TryDelete(partialVttPath);
                    TryDelete(BuildModelScopedPath(inputFile, options.Model, ".transcription.review.txt"));
                }

                string? lockedLanguage = null;
                if (!IsAutoLanguage(options.Language))
                {
                    lockedLanguage = options.Language.Trim();
                }
                else if (options.LockDetectedLanguage)
                {
                    lockedLanguage = DetectLanguageFromCompletedChunks(checkpoint.CompletedChunks);
                    if (!string.IsNullOrWhiteSpace(lockedLanguage))
                    {
                        _logger.Information("Restored detected language from checkpoint: {language}", lockedLanguage);
                    }
                }

                List<ChunkTranscriptionResult> completed = [];
                int reusedChunks = 0;
                int transcribedChunks = 0;

                for (int i = 0; i < chunks.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    AudioChunk chunk = chunks[i];

                    ChunkTranscriptionResult? cached = options.Resume
                        ? FindCachedChunk(checkpoint, chunk)
                        : null;

                    ChunkTranscriptionResult result;
                    if (cached is not null)
                    {
                        result = cached;
                        reusedChunks++;
                        _logger.Information("Chunk {chunkId} {start}-{end}: reused from checkpoint",
                            chunk.Id, FormatClock(chunk.Start), FormatClock(chunk.End));
                    }
                    else
                    {
                        _logger.Information("Chunk {chunkId}/{count} {start}-{end}: transcribing",
                            chunk.Id, chunks.Count, FormatClock(chunk.Start), FormatClock(chunk.End));

                        result = await TranscribeChunkWithRecoveryAsync(
                            normalized.FullName,
                            audioDuration,
                            chunk,
                            depth: 0,
                            options,
                            fallbackModels,
                            speechRegions,
                            models,
                            lockedLanguage,
                            token);

                        transcribedChunks++;
                        checkpoint.KnownHallucinationSegments = _dynamicHallucinationSegments.OrderBy(s => s).ToList();
                        checkpoint.CompletedChunks.RemoveAll(c => SameChunk(c.Chunk, chunk));
                        checkpoint.CompletedChunks.Add(result);
                        checkpoint.CompletedChunks = checkpoint.CompletedChunks
                            .OrderBy(c => c.Chunk.Start)
                            .ToList();
                        checkpoint.UpdatedUtc = DateTime.UtcNow;
                        await SaveJsonAtomicAsync(checkpointPath, checkpoint, CancellationToken.None);
                    }

                    completed.Add(result);
                    if (cached is null)
                    {
                        // On a partially invalidated checkpoint, later clean chunks may
                        // already be cached even though the loop has not traversed them yet.
                        // Write all currently known-good checkpoint results so partial output
                        // never regresses to only the prefix processed in this invocation.
                        await WriteProgressOutputsAsync(inputFile, options.Model, checkpoint.CompletedChunks, chunks.Count, CancellationToken.None);
                    }

                    if (IsAutoLanguage(options.Language) && options.LockDetectedLanguage && string.IsNullOrWhiteSpace(lockedLanguage))
                    {
                        string? detected = DetectLanguageFromResult(result);
                        if (!string.IsNullOrWhiteSpace(detected))
                        {
                            lockedLanguage = detected;
                            _logger.Information("Detected language locked for remaining chunks: {language}", lockedLanguage);
                        }
                    }
                }

                // A few Whisper hallucinations are not locally repetitive inside one chunk,
                // but recur verbatim across many distant chunks (for example subtitle-credit
                // boilerplate during silence). Discover those patterns after the first pass and
                // repair their chunks immediately instead of requiring a second CLI invocation.
                HashSet<string> newlyRecurring = FindRecurringCrossChunkSegments(completed);
                newlyRecurring.ExceptWith(_dynamicHallucinationSegments);
                // A fully cached run is a resume, not an implicit quality-improvement job.
                // Exhausted/review-marked cached outcomes must not restart their ladder.
                if (transcribedChunks > 0 && newlyRecurring.Count > 0)
                {
                    _dynamicHallucinationSegments.UnionWith(newlyRecurring);
                    List<int> repairIndexes = completed
                        .Select((result, index) => (result, index))
                        .Where(x => ContainsAnyRecurringSegment(x.result.Segments, newlyRecurring) &&
                                    !CheckpointReusePolicy.NeedsReview(x.result))
                        .Select(x => x.index)
                        .ToList();

                    _logger.Warning(
                        "Post-pass cross-chunk audit discovered {patterns} recurring hallucination patterns affecting {chunks} chunks. Repairing those chunks now.",
                        newlyRecurring.Count, repairIndexes.Count);
                    foreach (string recurring in newlyRecurring.OrderBy(x => x))
                    {
                        _logger.Warning("Recurring hallucination candidate: {segment}", TruncateForLog(recurring, 120));
                    }

                    foreach (int repairIndex in repairIndexes)
                    {
                        token.ThrowIfCancellationRequested();
                        AudioChunk repairChunk = chunks[repairIndex];
                        _logger.Warning(
                            "Chunk {chunkId}: retranscribing because its accepted text matched a recurring cross-chunk hallucination pattern",
                            repairChunk.Id);

                        ChunkTranscriptionResult repaired = await TranscribeChunkWithRecoveryAsync(
                            normalized.FullName,
                            audioDuration,
                            repairChunk,
                            depth: 0,
                            options,
                            fallbackModels,
                            speechRegions,
                            models,
                            lockedLanguage,
                            token);

                        completed[repairIndex] = repaired;
                        checkpoint.KnownHallucinationSegments = _dynamicHallucinationSegments.OrderBy(s => s).ToList();
                        checkpoint.CompletedChunks.RemoveAll(c => SameChunk(c.Chunk, repairChunk));
                        checkpoint.CompletedChunks.Add(repaired);
                        checkpoint.CompletedChunks = checkpoint.CompletedChunks.OrderBy(c => c.Chunk.Start).ToList();
                        checkpoint.UpdatedUtc = DateTime.UtcNow;
                        await SaveJsonAtomicAsync(checkpointPath, checkpoint, CancellationToken.None);
                        await WriteProgressOutputsAsync(inputFile, options.Model, checkpoint.CompletedChunks, chunks.Count, CancellationToken.None);
                    }

                    HashSet<string> remainingRecurring = FindRecurringCrossChunkSegments(completed);
                    remainingRecurring.IntersectWith(_dynamicHallucinationSegments);
                    if (remainingRecurring.Count > 0)
                    {
                        foreach (ChunkTranscriptionResult result in completed)
                        {
                            if (!ContainsAnyRecurringSegment(result.Segments, remainingRecurring))
                            {
                                continue;
                            }

                            result.NeedsReview = true;
                            result.Quality.Suspicious = true;
                            result.Quality.Score = Math.Max(result.Quality.Score, 0.85);
                            string reason = "recurring cross-chunk hallucination pattern remained after automatic repair";
                            if (!result.Quality.Reasons.Contains(reason, StringComparer.Ordinal))
                            {
                                result.Quality.Reasons.Add(reason);
                            }
                        }
                    }
                }

                var report = new TranscriptionRunReport
                {
                    InputPath = inputFile.FullName,
                    InputLength = inputFile.Length,
                    InputLastWriteUtc = inputFile.LastWriteTimeUtc,
                    Fingerprint = fingerprint,
                    StartedUtc = startedUtc,
                    CompletedUtc = DateTime.UtcNow,
                    PrimaryModel = options.Model.ToString(),
                    FallbackModels = fallbackModels.Select(m => m.ToString()).ToList(),
                    RequestedLanguage = options.Language,
                    LockedDetectedLanguage = lockedLanguage,
                    UsedVad = usedVad,
                    VadSpeechRegionCount = speechRegions.Count,
                    AudioDuration = audioDuration,
                    NeedsReview = completed.Any(c => c.NeedsReview),
                    Chunks = completed.OrderBy(c => c.Chunk.Start).ToList()
                };

                // Save the final audited flags too: previously post-pass NeedsReview changes
                // could exist only in the report, not in the last checkpoint.
                foreach (ChunkTranscriptionResult result in report.Chunks)
                    CheckpointReusePolicy.PropagateReviewFlags(result);
                report.NeedsReview = report.Chunks.Any(CheckpointReusePolicy.NeedsReview);
                checkpoint.CompletedChunks = report.Chunks;
                checkpoint.KnownHallucinationSegments = _dynamicHallucinationSegments.OrderBy(s => s).ToList();
                checkpoint.UpdatedUtc = DateTime.UtcNow;
                await SaveJsonAtomicAsync(checkpointPath, checkpoint, CancellationToken.None);

                _logger.Information("Processing summary: {reused} chunks reused; {transcribed} newly processed; {review} completed with review flags. Review flags do not trigger automatic retries on the next run.",
                    reusedChunks, transcribedChunks, report.Chunks.Count(CheckpointReusePolicy.NeedsReview));
                return await WriteOutputsAsync(inputFile, options.Model, report, token);
            }
            finally
            {
                TryDelete(normalized.FullName);
            }
        }

        private async Task<ChunkTranscriptionResult> TranscribeChunkWithRecoveryAsync(
            string normalizedWavPath,
            TimeSpan audioDuration,
            AudioChunk chunk,
            int depth,
            AppOptions options,
            IReadOnlyList<GgmlType> fallbackModels,
            IReadOnlyList<SpeechRegion> speechRegions,
            ModelFileCache models,
            string? lockedLanguage,
            CancellationToken token)
        {
            List<AttemptDiagnostic> diagnostics = [];
            AttemptCandidate? best = null;

            List<AttemptProfile> profiles =
            [
                new(options.Model, "primary-limited-context", false, 0.25, 0.25),
                new(options.Model, "primary-no-context", true, 0.25, 0.25),
                new(options.Model, "primary-no-context-shifted-boundary", true, 1.50, 0.75)
            ];

            foreach (GgmlType fallback in fallbackModels)
            {
                profiles.Add(new AttemptProfile(fallback, "fallback-model-no-context", true, 0.75, 0.75));
            }

            foreach (AttemptProfile profile in profiles)
            {
                token.ThrowIfCancellationRequested();
                AttemptCandidate candidate = await RunAttemptAsync(
                    normalizedWavPath,
                    audioDuration,
                    chunk,
                    profile,
                    options,
                    models,
                    lockedLanguage,
                    token);

                diagnostics.Add(candidate.Diagnostic);
                best = best is null || candidate.Quality.Score < best.Quality.Score ? candidate : best;

                if (!candidate.Quality.Suspicious && candidate.Diagnostic.Error is null)
                {
                    _logger.Information(
                        "Chunk {chunkId}: accepted {model}/{strategy} (quality score {score:F2})",
                        chunk.Id, profile.Model, profile.Strategy, candidate.Quality.Score);
                    return CreateAcceptedChunkResult(chunk, candidate, diagnostics, needsReview: false);
                }

                string reasons = candidate.Quality.Reasons.Count == 0
                    ? candidate.Diagnostic.Error ?? "unknown quality failure"
                    : string.Join("; ", candidate.Quality.Reasons);
                _logger.Warning(
                    "Chunk {chunkId}: rejected {model}/{strategy} (score {score:F2}): {reasons}",
                    chunk.Id, profile.Model, profile.Strategy, candidate.Quality.Score, reasons);
            }

            bool canSplit = depth < options.MaxRecoveryDepth &&
                            chunk.Duration >= TimeSpan.FromSeconds(options.MinRecoverySplitSeconds * 2);
            if (canSplit)
            {
                TimeSpan split = ChunkPlanner.FindRecoverySplitPoint(
                    chunk,
                    speechRegions,
                    TimeSpan.FromSeconds(options.MinRecoverySplitSeconds));

                if (split > chunk.Start && split < chunk.End)
                {
                    _logger.Warning("Chunk {chunkId}: all direct attempts suspicious; recursively splitting at {split}",
                        chunk.Id, FormatClock(split));

                    AudioChunk left = new()
                    {
                        Id = chunk.Id + ".1",
                        Start = chunk.Start,
                        End = split,
                        ExpectedSpeech = speechRegions.Count > 0
                            ? HasSpeechOverlap(speechRegions, chunk.Start, split)
                            : chunk.ExpectedSpeech
                    };
                    AudioChunk right = new()
                    {
                        Id = chunk.Id + ".2",
                        Start = split,
                        End = chunk.End,
                        ExpectedSpeech = speechRegions.Count > 0
                            ? HasSpeechOverlap(speechRegions, split, chunk.End)
                            : chunk.ExpectedSpeech
                    };

                    ChunkTranscriptionResult leftResult = await TranscribeChunkWithRecoveryAsync(
                        normalizedWavPath, audioDuration, left, depth + 1, options, fallbackModels,
                        speechRegions, models, lockedLanguage, token);
                    ChunkTranscriptionResult rightResult = await TranscribeChunkWithRecoveryAsync(
                        normalizedWavPath, audioDuration, right, depth + 1, options, fallbackModels,
                        speechRegions, models, lockedLanguage, token);

                    List<TranscriptSegment> merged = leftResult.Segments
                        .Concat(rightResult.Segments)
                        .OrderBy(s => s.Start)
                        .ToList();
                    QualityAssessment mergedQuality = TranscriptionQualityAnalyzer.Analyze(
                        merged, chunk.Duration, chunk.ExpectedSpeech, options.GlitchThreshold);
                    ApplyDynamicHallucinationPenalty(mergedQuality, merged, _dynamicHallucinationSegments);

                    return new ChunkTranscriptionResult
                    {
                        Chunk = chunk,
                        SelectedModel = "multiple",
                        SelectedStrategy = "recursive-split-recovery",
                        NeedsReview = leftResult.NeedsReview || rightResult.NeedsReview || mergedQuality.Suspicious,
                        Quality = mergedQuality,
                        Segments = merged,
                        Attempts = diagnostics,
                        SubChunks = [leftResult, rightResult]
                    };
                }
            }

            if (best is null)
            {
                best = AttemptCandidate.Failed("No transcription attempt could be started.", chunk, options.GlitchThreshold);
            }

            _logger.Error(
                "Chunk {chunkId}: recovery ladder exhausted. Keeping the least suspicious candidate and marking the interval for review.",
                chunk.Id);
            return CreateAcceptedChunkResult(chunk, best, diagnostics, needsReview: true);
        }

        private async Task<AttemptCandidate> RunAttemptAsync(
            string normalizedWavPath,
            TimeSpan audioDuration,
            AudioChunk coverage,
            AttemptProfile profile,
            AppOptions options,
            ModelFileCache models,
            string? lockedLanguage,
            CancellationToken token)
        {
            TimeSpan audioStart = Max(TimeSpan.Zero, coverage.Start - TimeSpan.FromSeconds(profile.PreRollSeconds));
            TimeSpan audioEnd = Min(audioDuration, coverage.End + TimeSpan.FromSeconds(profile.PostRollSeconds));
            Stopwatch sw = Stopwatch.StartNew();

            var diagnostic = new AttemptDiagnostic
            {
                Model = profile.Model.ToString(),
                Strategy = profile.Strategy,
                AudioStart = audioStart,
                AudioEnd = audioEnd
            };

            string? chunkWavPath = null;
            string? requestPath = null;
            string? resultPath = null;
            try
            {
                FileInfo modelFile = await models.GetAsync(profile.Model, token);
                chunkWavPath = await ExtractChunkToStandaloneWaveFileAsync(
                    normalizedWavPath, audioStart, audioEnd, coverage.Id, token);

                string workerDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI", "Worker");
                Directory.CreateDirectory(workerDirectory);
                string attemptId = $"{coverage.Id}-{profile.Model}-{Guid.NewGuid():N}";
                requestPath = Path.Combine(workerDirectory, attemptId + ".request.json");
                resultPath = Path.Combine(workerDirectory, attemptId + ".result.json");

                var request = new WhisperWorkerRequest
                {
                    ModelPath = modelFile.FullName,
                    AudioPath = chunkWavPath,
                    Language = string.IsNullOrWhiteSpace(lockedLanguage) ? options.Language : lockedLanguage,
                    Runtime = EffectiveWorkerRuntime(options.Runtime),
                    GpuDevice = options.GpuDevice,
                    NoContext = profile.NoContext,
                    EntropyThreshold = options.EntropyThreshold,
                    Temperature = options.Temperature,
                    TemperatureIncrement = options.TemperatureIncrement,
                    ResultPath = resultPath
                };

                await File.WriteAllTextAsync(
                    requestPath,
                    JsonSerializer.Serialize(request, JsonOptions),
                    Encoding.UTF8,
                    token);

                _logger.Information(
                    "Chunk {chunkId}: launching isolated Whisper worker for {model}/{strategy} on {duration:0.0}s of audio",
                    coverage.Id, profile.Model, profile.Strategy, (audioEnd - audioStart).TotalSeconds);

                WhisperWorkerResult workerResult = await RunWhisperWorkerProcessAsync(
                    requestPath, resultPath, coverage.Id, profile.Model, profile.Strategy, sw, token);

                if (!string.IsNullOrWhiteSpace(workerResult.ManagedError))
                {
                    throw new WhisperRuntimeUnavailableException(
                        $"Isolated Whisper worker reported a managed failure for chunk {coverage.Id}:\n{workerResult.ManagedError}");
                }

                string loadedRuntime = string.IsNullOrWhiteSpace(workerResult.LoadedRuntime)
                    ? "<unknown>"
                    : workerResult.LoadedRuntime;
                bool gpu = loadedRuntime.Equals("Cuda", StringComparison.OrdinalIgnoreCase) ||
                           loadedRuntime.Equals("Vulkan", StringComparison.OrdinalIgnoreCase);

                if (!string.Equals(loadedRuntime, _lastWorkerRuntime, StringComparison.OrdinalIgnoreCase))
                {
                    _lastWorkerRuntime = loadedRuntime;
                    _logger.Information(
                        "Isolated Whisper worker runtime: {runtime}; GPU acceleration active={gpu}; native crashes cannot terminate the parent CLI",
                        loadedRuntime, gpu);
                }

                string requestedRuntime = (options.Runtime ?? "auto").Trim().ToLowerInvariant();
                bool gpuRequired = requestedRuntime is "gpu" or "nvidia" or "cuda" or "cuda12" or "vulkan" ||
                                   (requestedRuntime == "auto" && WhisperRuntimeManager.NvidiaDetected);
                if (gpuRequired && !gpu)
                {
                    throw new WhisperRuntimeUnavailableException(
                        $"An NVIDIA GPU is present/requested, but the isolated worker loaded '{loadedRuntime}'. " +
                        "WhisperCLI will not silently continue the Large model on CPU. Use --runtime cpu only if CPU inference is intentional.");
                }

                List<TranscriptSegment> rawSegments = workerResult.Segments
                    .Select(s => new TranscriptSegment
                    {
                        Start = audioStart + s.Start,
                        End = audioStart + s.End,
                        Text = s.Text,
                        Language = s.Language
                    })
                    .OrderBy(s => s.Start)
                    .ToList();

                diagnostic.AbortedForLiveLoop = workerResult.LiveLoopAborted;

                _logger.Information(
                    "Chunk {chunkId}: isolated Whisper worker returned {segments} raw segments after {elapsed:0.0}s",
                    coverage.Id, rawSegments.Count, sw.Elapsed.TotalSeconds);

                token.ThrowIfCancellationRequested();

                List<TranscriptSegment> selectedSegments = rawSegments
                    .Where(s => SegmentBelongsToCoverage(s, coverage, audioDuration))
                    .OrderBy(s => s.Start)
                    .ToList();

                QualityAssessment quality = TranscriptionQualityAnalyzer.Analyze(
                    selectedSegments,
                    coverage.Duration,
                    coverage.ExpectedSpeech,
                    options.GlitchThreshold,
                    workerResult.LiveLoopAborted);
                ApplyDynamicHallucinationPenalty(quality, selectedSegments, _dynamicHallucinationSegments);

                sw.Stop();
                diagnostic.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                diagnostic.Quality = quality;
                return new AttemptCandidate(profile, selectedSegments, quality, diagnostic);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WhisperRuntimeUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The robust recovery ladder is for *valid but suspicious text*, not execution
                // failures. Once a worker/model/audio/backend fails to execute, stop the run.
                throw new WhisperRuntimeUnavailableException(
                    $"Whisper execution failed for chunk {coverage.Id} using {profile.Model}/{profile.Strategy}. " +
                    "No checkpoint entry was written for this chunk.", ex);
            }
            finally
            {
                if (chunkWavPath is not null) TryDelete(chunkWavPath);
                if (requestPath is not null) TryDelete(requestPath);
                if (resultPath is not null) TryDelete(resultPath);
            }
        }

        private async Task<WhisperWorkerResult> RunWhisperWorkerProcessAsync(
            string requestPath,
            string resultPath,
            string chunkId,
            GgmlType model,
            string strategy,
            Stopwatch stopwatch,
            CancellationToken token)
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string processPath = Environment.ProcessPath ?? "dotnet";
            bool hostedByDotnet = string.Equals(
                Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

            ProcessStartInfo startInfo = new()
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            // Linux/NVIDIA workers are CUDA-only. Whisper.net 1.8.1 requires external
            // CUDA 12 runtime/cuBLAS libraries; discover their existing locations and inject
            // those directories into the child process before Whisper.net probes CUDA.
            if (ShouldUseLinuxNvidiaCudaOnly())
            {
                CudaRuntimeLocator.ApplyTo(startInfo);
                if (!_cudaLibraryGuardLogged)
                {
                    _cudaLibraryGuardLogged = true;
                    CudaRuntimeProbe probe = CudaRuntimeLocator.Probe();
                    _logger.Information(
                        "Linux NVIDIA CUDA worker guard enabled: Vulkan disabled; CUDA_VISIBLE_DEVICES=0; LD_LIBRARY_PATH augmented with {dirs}",
                        probe.LibraryDirectories.Count == 0 ? "<system loader paths>" : string.Join(Path.PathSeparator, probe.LibraryDirectories));
                }
            }
            if (hostedByDotnet)
            {
                startInfo.ArgumentList.Add(assemblyPath);
            }
            startInfo.ArgumentList.Add(InternalWhisperWorker.Switch);
            startInfo.ArgumentList.Add(requestPath);

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new WhisperRuntimeUnavailableException("Failed to start isolated Whisper worker process.");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The parent cancellation token remains authoritative.
                }
            });

            using CancellationTokenSource heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task heartbeat = LogWorkerHeartbeatAsync(chunkId, model, strategy, stopwatch, process, heartbeatCts.Token);

            try
            {
                await process.WaitForExitAsync(token);
            }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            token.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                string diagnosis = DiagnoseWorkerBackendFailure(stderr);
                throw new WhisperRuntimeUnavailableException(
                    $"Isolated Whisper worker terminated abnormally (exit code {process.ExitCode}) while processing " +
                    $"chunk {chunkId} with {model}/{strategy}. This is a native/backend failure, not a bad transcription. " +
                    $"The parent CLI survived and did not checkpoint the chunk." +
                    (string.IsNullOrWhiteSpace(diagnosis) ? string.Empty : $"\nBackend diagnosis: {diagnosis}") +
                    $"\nWorker stdout tail:\n{Tail(stdout, 4000)}\n" +
                    $"Worker stderr/native tail:\n{Tail(stderr, 12000)}");
            }

            if (!File.Exists(resultPath))
            {
                throw new WhisperRuntimeUnavailableException(
                    $"Isolated Whisper worker exited successfully but produced no result file for chunk {chunkId}.\n" +
                    $"Worker stdout tail:\n{Tail(stdout, 4000)}\nWorker stderr tail:\n{Tail(stderr, 8000)}");
            }

            WhisperWorkerResult? result = JsonSerializer.Deserialize<WhisperWorkerResult>(
                await File.ReadAllTextAsync(resultPath, token), JsonOptions);
            if (result is null)
            {
                throw new WhisperRuntimeUnavailableException(
                    $"Isolated Whisper worker produced an unreadable result for chunk {chunkId}.");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.Debug("Chunk {chunkId} worker diagnostic tail:\n{stderr}", chunkId, Tail(stderr, 6000));
            }
            return result;
        }

        private static bool ShouldUseLinuxNvidiaCudaOnly()
        {
            if (!OperatingSystem.IsLinux() || !WhisperRuntimeManager.NvidiaDetected)
            {
                return false;
            }

            string runtime = (WhisperRuntimeManager.RuntimePreference ?? "auto").Trim().ToLowerInvariant();
            return runtime is "auto" or "gpu" or "nvidia" or "cuda" or "cuda12";
        }

        private static string EffectiveWorkerRuntime(string? requestedRuntime)
        {
            string runtime = (requestedRuntime ?? "auto").Trim().ToLowerInvariant();
            if (OperatingSystem.IsLinux() && WhisperRuntimeManager.NvidiaDetected &&
                runtime is "auto" or "gpu" or "nvidia")
            {
                return "cuda";
            }
            return runtime;
        }

        private static string DiagnoseWorkerBackendFailure(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return string.Empty;
            }

            List<string> notes = [];

            if (stderr.Contains("Cudart library couldn't be loaded", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("Whisper.net could not load CUDA's libcudart even after CUDA library-path discovery; inspect the preflight LD_LIBRARY_PATH/ldd diagnostics");
            }

            if (stderr.Contains("[worker] loaded runtime: Cuda", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("ggml_cuda", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("CUDA backend was selected; any exit 139 after this point is a native CUDA/whisper.cpp crash rather than runtime fallback");
            }

            string? vulkan0 = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => line.Contains("ggml_vulkan:", StringComparison.OrdinalIgnoreCase) &&
                                       line.Contains("0 =", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(vulkan0))
            {
                if (vulkan0.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        "Vulkan device 0 is still Intel; the NVIDIA PRIME Vulkan guard did not take effect. " +
                        "The NVIDIA Vulkan Optimus layer/ICD should be checked");
                }
                else if (vulkan0.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        "Vulkan device 0 is NVIDIA, so GPU selection is correct; the remaining crash is inside the NVIDIA Vulkan/native backend");
                }
            }

            return string.Join("; ", notes);
        }

        private async Task LogWorkerHeartbeatAsync(
            string chunkId,
            GgmlType model,
            string strategy,
            Stopwatch stopwatch,
            Process process,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                if (!process.HasExited)
                {
                    _logger.Information(
                        "Chunk {chunkId}: isolated Whisper worker is still running after {elapsed:0}s ({model}/{strategy}, pid={pid})",
                        chunkId, stopwatch.Elapsed.TotalSeconds, model, strategy, process.Id);
                }
            }
        }

        private async Task<string> ExtractChunkToStandaloneWaveFileAsync(
            string normalizedWavPath,
            TimeSpan start,
            TimeSpan end,
            string chunkId,
            CancellationToken token)
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI", "WorkerAudio");
            Directory.CreateDirectory(workingDirectory);
            string outputPath = Path.Combine(workingDirectory, $"{chunkId}-{Guid.NewGuid():N}.wav");
            string ffmpegPath = Path.Combine(
                FFmpeg.ExecutablesPath,
                OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-y");
            // Input-side seeking is exact for the normalized PCM WAV and avoids decoding from
            // 00:00 for every late-file chunk.
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(start.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(normalizedWavPath);
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add((end - start).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("16000");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("wav");
            startInfo.ArgumentList.Add(outputPath);

            try
            {
                using Process process = new() { StartInfo = startInfo };
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start FFmpeg chunk extraction.");
                }

                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                using CancellationTokenRegistration registration = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });
                await process.WaitForExitAsync(token);
                string stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg failed to extract chunk {chunkId} (exit code {process.ExitCode}).\n{Tail(stderr, 8000)}");
                }

                FileInfo file = new(outputPath);
                file.Refresh();
                if (!file.Exists || file.Length <= 44)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg produced an empty/invalid WAV for chunk {chunkId} at '{outputPath}'.");
                }

                // Validate the exact stream that will cross the native boundary. A malformed or
                // header-only WAV can cause whisper.cpp to fail below managed exception handling.
                using (var reader = new WaveFileReader(outputPath))
                {
                    WaveFormat format = reader.WaveFormat;
                    if (format.SampleRate != 16000 || format.Channels != 1 ||
                        format.BitsPerSample != 16 || format.Encoding != WaveFormatEncoding.Pcm)
                    {
                        throw new InvalidOperationException(
                            $"Chunk {chunkId} WAV has unexpected format: {format}. Expected 16 kHz mono PCM16.");
                    }

                    if (reader.TotalTime < TimeSpan.FromMilliseconds(250))
                    {
                        throw new InvalidOperationException(
                            $"Chunk {chunkId} WAV contains too little audio ({reader.TotalTime.TotalMilliseconds:0} ms).");
                    }

                    double durationDelta = Math.Abs((reader.TotalTime - (end - start)).TotalSeconds);
                    if (durationDelta > 1.0)
                    {
                        throw new InvalidOperationException(
                            $"Chunk {chunkId} WAV duration mismatch: expected {(end - start).TotalSeconds:0.000}s, " +
                            $"got {reader.TotalTime.TotalSeconds:0.000}s.");
                    }

                    _logger.Debug(
                        "Chunk {chunkId}: standalone WAV validated: {bytes} bytes, {duration:0.000}s, {format}",
                        chunkId, file.Length, reader.TotalTime.TotalSeconds, format);
                }

                return outputPath;
            }
            catch
            {
                TryDelete(outputPath);
                throw;
            }
        }

        private List<SpeechRegion> DetectSpeechRegions(FileInfo normalizedWav, AppOptions options, CancellationToken token)
        {
            // Whisper.net 1.9.1 introduced Silero VAD, but the original application used
            // Whisper.net 1.8.1 and its CUDA 12.x runtime successfully on this machine.
            // Keep that known-good GPU runtime and use a small managed detector only to find
            // quiet gaps for chunk boundaries. IMPORTANT: the detector never decides which
            // audio gets transcribed; every chunk is still sent to Whisper.
            using var reader = new WaveFileReader(normalizedWav.FullName);
            WaveFormat format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels != 1)
            {
                throw new InvalidDataException(
                    $"Managed boundary detector expects mono PCM16 WAV; got {format.Encoding}, {format.BitsPerSample}-bit, {format.Channels} channel(s).");
            }

            const int frameMs = 30;
            int samplesPerFrame = Math.Max(1, format.SampleRate * frameMs / 1000);
            int bytesPerFrame = samplesPerFrame * 2;
            byte[] buffer = new byte[bytesPerFrame];
            List<EnergyFrame> frames = [];
            long sampleCursor = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = reader.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }

                if (read < 2)
                {
                    break;
                }

                int sampleCount = read / 2;
                double sumSquares = 0;
                for (int i = 0; i < sampleCount * 2; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    double normalized = sample / 32768.0;
                    sumSquares += normalized * normalized;
                }

                double rms = Math.Sqrt(sumSquares / sampleCount);
                double dbfs = 20.0 * Math.Log10(Math.Max(rms, 1e-9));
                TimeSpan start = TimeSpan.FromSeconds((double)sampleCursor / format.SampleRate);
                sampleCursor += sampleCount;
                TimeSpan end = TimeSpan.FromSeconds((double)sampleCursor / format.SampleRate);
                frames.Add(new EnergyFrame(start, end, dbfs));

                if (read < buffer.Length)
                {
                    break;
                }
            }

            if (frames.Count == 0)
            {
                return [];
            }

            double[] sortedDb = frames.Select(f => f.Dbfs).OrderBy(v => v).ToArray();
            double noiseFloor = Percentile(sortedDb, 0.20);
            double activeLevel = Percentile(sortedDb, 0.90);
            double dynamicRange = Math.Max(0, activeLevel - noiseFloor);

            // --vad-threshold remains a 0..1 sensitivity knob for CLI compatibility.
            // At the default 0.5, activity needs roughly 8 dB above the estimated floor.
            // This detector is intentionally conservative because it is only choosing gaps.
            double sensitivity = Math.Clamp(options.VadThreshold, 0.01f, 0.99f);
            double marginDb = 4.0 + 8.0 * sensitivity;
            marginDb = Math.Min(marginDb, Math.Max(4.0, dynamicRange * 0.45));
            double thresholdDb = Math.Clamp(noiseFloor + marginDb, -60.0, -20.0);
            if (activeLevel - thresholdDb < 3.0)
            {
                thresholdDb = activeLevel - 3.0;
            }

            _logger.Debug(
                "Managed boundary detector levels: noise={noise:F1} dBFS, active={active:F1} dBFS, threshold={threshold:F1} dBFS",
                noiseFloor, activeLevel, thresholdDb);

            TimeSpan minSpeech = TimeSpan.FromMilliseconds(options.VadMinSpeechMs);
            TimeSpan minSilence = TimeSpan.FromMilliseconds(options.VadMinSilenceMs);
            TimeSpan padding = TimeSpan.FromMilliseconds(options.VadSpeechPaddingMs);

            List<SpeechRegion> rawRegions = [];
            TimeSpan? regionStart = null;
            TimeSpan? quietStart = null;
            TimeSpan lastEnd = TimeSpan.Zero;

            foreach (EnergyFrame frame in frames)
            {
                token.ThrowIfCancellationRequested();
                lastEnd = frame.End;
                bool active = frame.Dbfs >= thresholdDb;

                if (active)
                {
                    regionStart ??= frame.Start;
                    quietStart = null;
                    continue;
                }

                if (regionStart is null)
                {
                    continue;
                }

                quietStart ??= frame.Start;
                if (frame.End - quietStart.Value >= minSilence)
                {
                    TimeSpan regionEnd = quietStart.Value;
                    if (regionEnd - regionStart.Value >= minSpeech)
                    {
                        rawRegions.Add(new SpeechRegion { Start = regionStart.Value, End = regionEnd });
                    }
                    regionStart = null;
                    quietStart = null;
                }
            }

            if (regionStart is not null)
            {
                TimeSpan regionEnd = quietStart ?? lastEnd;
                if (regionEnd - regionStart.Value >= minSpeech)
                {
                    rawRegions.Add(new SpeechRegion { Start = regionStart.Value, End = regionEnd });
                }
            }

            if (rawRegions.Count == 0)
            {
                return [];
            }

            // Pad and merge. Max speech duration is deliberately NOT enforced here; ChunkPlanner
            // already imposes the hard chunk maximum independently of these activity regions.
            TimeSpan total = reader.TotalTime;
            List<SpeechRegion> padded = [];
            foreach (SpeechRegion region in rawRegions)
            {
                TimeSpan start = Max(TimeSpan.Zero, region.Start - padding);
                TimeSpan end = Min(total, region.End + padding);
                if (padded.Count > 0 && start <= padded[^1].End)
                {
                    padded[^1].End = Max(padded[^1].End, end);
                }
                else
                {
                    padded.Add(new SpeechRegion { Start = start, End = end });
                }
            }

            return padded;
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return -120.0;
            }

            double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
            {
                return sorted[lower];
            }
            double fraction = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
        }

        private readonly record struct EnergyFrame(TimeSpan Start, TimeSpan End, double Dbfs);

        private async Task WriteProgressOutputsAsync(
            FileInfo inputFile,
            GgmlType primaryModel,
            IReadOnlyList<ChunkTranscriptionResult> completedChunks,
            int totalChunks,
            CancellationToken token)
        {
            List<TranscriptSegment> segments = completedChunks
                .SelectMany(FlattenSegments)
                .OrderBy(s => s.Start)
                .ToList();

            string textPath = BuildModelScopedPath(inputFile, primaryModel, ".partial.txt");
            string srtPath = BuildModelScopedPath(inputFile, primaryModel, ".partial.srt");
            string vttPath = BuildModelScopedPath(inputFile, primaryModel, ".partial.vtt");

            await WriteTextAtomicAsync(textPath, AssembleText(segments), token);
            await WriteTextAtomicAsync(srtPath, BuildSrt(segments), token);
            await WriteTextAtomicAsync(vttPath, BuildVtt(segments), token);

            int completedTopLevel = completedChunks.Count;
            _logger.Information(
                "Progress saved: {completed}/{total} chunks -> {partialTextPath}",
                completedTopLevel, totalChunks, textPath);
        }

        private async Task<FileInfo> WriteOutputsAsync(
            FileInfo inputFile,
            GgmlType primaryModel,
            TranscriptionRunReport report,
            CancellationToken token)
        {
            List<TranscriptSegment> segments = report.Chunks
                .SelectMany(FlattenSegments)
                .OrderBy(s => s.Start)
                .ToList();

            string textPath = BuildModelScopedPath(inputFile, primaryModel, ".txt");
            string srtPath = BuildModelScopedPath(inputFile, primaryModel, ".srt");
            string vttPath = BuildModelScopedPath(inputFile, primaryModel, ".vtt");
            string jsonPath = BuildModelScopedPath(inputFile, primaryModel, ".transcription.json");
            string logPath = BuildModelScopedPath(inputFile, primaryModel, ".transcription.log");
            string reviewPath = BuildModelScopedPath(inputFile, primaryModel, ".transcription.review.txt");

            string text = AssembleText(segments);
            await WriteTextAtomicAsync(textPath, text, token);
            await WriteTextAtomicAsync(srtPath, BuildSrt(segments), token);
            await WriteTextAtomicAsync(vttPath, BuildVtt(segments), token);
            await SaveJsonAtomicAsync(jsonPath, report, token);
            await File.WriteAllTextAsync(logPath, BuildDiagnosticLog(report), Encoding.UTF8, token);

            if (report.NeedsReview)
            {
                await File.WriteAllTextAsync(reviewPath, BuildReviewFile(report), Encoding.UTF8, token);
                _logger.Warning("Transcription completed, but one or more intervals still need review: {reviewPath}", reviewPath);
            }
            else
            {
                TryDelete(reviewPath);
            }

            TryDelete(BuildModelScopedPath(inputFile, primaryModel, ".partial.txt"));
            TryDelete(BuildModelScopedPath(inputFile, primaryModel, ".partial.srt"));
            TryDelete(BuildModelScopedPath(inputFile, primaryModel, ".partial.vtt"));

            _logger.Information("Transcription complete: {textPath}", textPath);
            _logger.Information("Subtitles: {srtPath} and {vttPath}", srtPath, vttPath);
            _logger.Information("Diagnostics: {jsonPath} and {logPath}", jsonPath, logPath);
            return new FileInfo(textPath);
        }

        private static IEnumerable<TranscriptSegment> FlattenSegments(ChunkTranscriptionResult result)
        {
            // Top-level recursive-split results already contain the merged child segments.
            return result.Segments;
        }

        private static string BuildSrt(IReadOnlyList<TranscriptSegment> segments)
        {
            StringBuilder sb = new();
            int index = 1;
            foreach (TranscriptSegment segment in segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                sb.AppendLine(index.ToString());
                sb.Append(FormatSubtitleTime(segment.Start, ',')).Append(" --> ").AppendLine(FormatSubtitleTime(segment.End, ','));
                sb.AppendLine(segment.Text.Trim());
                sb.AppendLine();
                index++;
            }
            return sb.ToString();
        }

        private static string BuildVtt(IReadOnlyList<TranscriptSegment> segments)
        {
            StringBuilder sb = new("WEBVTT\n\n");
            foreach (TranscriptSegment segment in segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                sb.Append(FormatSubtitleTime(segment.Start, '.')).Append(" --> ").AppendLine(FormatSubtitleTime(segment.End, '.'));
                sb.AppendLine(segment.Text.Trim());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string BuildDiagnosticLog(TranscriptionRunReport report)
        {
            StringBuilder sb = new();
            sb.AppendLine($"Input: {report.InputPath}");
            sb.AppendLine($"Audio duration: {FormatClock(report.AudioDuration)}");
            sb.AppendLine($"Primary model: {report.PrimaryModel}");
            sb.AppendLine($"Fallback models: {string.Join(", ", report.FallbackModels)}");
            sb.AppendLine($"Requested language: {report.RequestedLanguage}");
            sb.AppendLine($"Locked detected language: {report.LockedDetectedLanguage ?? "<none>"}");
            sb.AppendLine($"Boundary detection: {(report.UsedVad ? "managed energy/silence" : "fixed chunks")}, activity regions={report.VadSpeechRegionCount}");
            sb.AppendLine($"Processing status: {report.CompletionStatus}");
            sb.AppendLine($"Needs review: {report.NeedsReview}");
            sb.AppendLine();

            foreach (ChunkTranscriptionResult chunk in report.Chunks)
            {
                AppendChunkLog(sb, chunk, 0);
            }
            return sb.ToString();
        }

        private static void AppendChunkLog(StringBuilder sb, ChunkTranscriptionResult chunk, int indent)
        {
            string pad = new(' ', indent * 2);
            sb.AppendLine($"{pad}Chunk {chunk.Chunk.Id} {FormatClock(chunk.Chunk.Start)}-{FormatClock(chunk.Chunk.End)} " +
                          $"selected={chunk.SelectedModel}/{chunk.SelectedStrategy} score={chunk.Quality.Score:F2} review={chunk.NeedsReview}");
            if (chunk.Quality.Reasons.Count > 0)
            {
                sb.AppendLine($"{pad}  selected reasons: {string.Join("; ", chunk.Quality.Reasons)}");
            }
            foreach (AttemptDiagnostic attempt in chunk.Attempts)
            {
                string reasons = attempt.Quality.Reasons.Count > 0 ? string.Join("; ", attempt.Quality.Reasons) : "clean";
                sb.AppendLine($"{pad}  attempt {attempt.Model}/{attempt.Strategy}: score={attempt.Quality.Score:F2}, " +
                              $"elapsed={attempt.ElapsedSeconds:F1}s, liveAbort={attempt.AbortedForLiveLoop}, {reasons}");
                if (!string.IsNullOrWhiteSpace(attempt.Error))
                {
                    sb.AppendLine($"{pad}    ERROR: {attempt.Error}");
                }
            }
            foreach (ChunkTranscriptionResult child in chunk.SubChunks)
            {
                AppendChunkLog(sb, child, indent + 1);
            }
        }

        private static string BuildReviewFile(TranscriptionRunReport report)
        {
            StringBuilder sb = new();
            sb.AppendLine("The following intervals need review (recovery exhausted or a quality audit remained suspicious):");
            sb.AppendLine("Processing is complete. Ordinary resume reuses these outcomes. Use --retry-review to retry them explicitly.");
            sb.AppendLine();
            foreach (ChunkTranscriptionResult chunk in report.Chunks.Where(c => c.NeedsReview))
            {
                AppendReviewEntries(sb, chunk);
            }
            return sb.ToString();
        }

        private static void AppendReviewEntries(StringBuilder sb, ChunkTranscriptionResult chunk)
        {
            // A merged parent can be suspicious even when all its children passed alone.
            if (chunk.NeedsReview && (chunk.SubChunks.Count == 0 || !chunk.SubChunks.Any(c => c.NeedsReview)))
            {
                sb.AppendLine($"{FormatClock(chunk.Chunk.Start)} - {FormatClock(chunk.Chunk.End)} | " +
                              $"{chunk.SelectedModel}/{chunk.SelectedStrategy} | score={chunk.Quality.Score:F2}");
                if (chunk.Quality.Reasons.Count > 0)
                {
                    sb.AppendLine("  " + string.Join("; ", chunk.Quality.Reasons));
                }
            }
            foreach (ChunkTranscriptionResult child in chunk.SubChunks.Where(c => c.NeedsReview))
            {
                AppendReviewEntries(sb, child);
            }
        }

        private static string AssembleText(IReadOnlyList<TranscriptSegment> segments)
        {
            StringBuilder sb = new();
            foreach (TranscriptSegment segment in segments)
            {
                AppendText(sb, segment.Text);
            }
            return sb.ToString().Trim();
        }

        private static void AppendText(StringBuilder sb, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string value = text.Trim();
            if (sb.Length == 0)
            {
                sb.Append(value);
                return;
            }

            char first = value[0];
            char last = sb[^1];
            bool punctuationPrefix = ".,!?;:)]}»”.…".Contains(first);
            if (!char.IsWhiteSpace(last) && !punctuationPrefix)
            {
                sb.Append(' ');
            }
            sb.Append(value);
        }

        private async Task CheckFfmpegAsync(CancellationToken token)
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI", "FFMpeg");
            Directory.CreateDirectory(workingDirectory);
            FFmpeg.SetExecutablesPath(workingDirectory);

            string ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            string ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            string ffmpegPath = Path.Combine(workingDirectory, ffmpegName);
            string ffprobePath = Path.Combine(workingDirectory, ffprobeName);

            _logger.Information("Checking FFmpeg...");
            if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            {
                _logger.Information("FFmpeg not found - downloading...");
                Task downloadTask = FFmpegDownloader.GetLatestVersion(
                    FFmpegVersion.Official,
                    FFmpeg.ExecutablesPath,
                    new FFMpegDownloadingProgress(_logger));
                Task timeoutTask = Task.Delay(TimeSpan.FromMinutes(10), token);
                Task completed = await Task.WhenAny(downloadTask, timeoutTask);
                token.ThrowIfCancellationRequested();
                if (completed != downloadTask)
                {
                    throw new TimeoutException("FFmpeg download did not complete within 10 minutes.");
                }
                await downloadTask;
                _logger.Information("FFmpeg downloaded");
            }

            if (!OperatingSystem.IsWindows())
            {
                EnsureExecutable(ffmpegPath);
                EnsureExecutable(ffprobePath);
            }
        }

        private async Task<FileInfo> ConvertToWaveFileAsync(FileInfo inputFile, CancellationToken token)
        {
            _logger.Information("Preparing temporary WAV (16 kHz, mono, PCM16) from: {inputFile}", inputFile.FullName);
            string workingDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI", "Working");
            Directory.CreateDirectory(workingDirectory);
            string tempOutputPath = Path.Combine(workingDirectory, $"{Guid.NewGuid():N}.wav");

            // Do not use Xabe's conversion argument builder here. Long-file transcription accepts
            // arbitrary user paths (spaces, Cyrillic, etc.), and this call is simple enough that
            // invoking ffmpeg directly is both safer and easier to diagnose. ArgumentList passes
            // each value verbatim without shell parsing or hand-written quoting.
            string ffmpegPath = Path.Combine(
                FFmpeg.ExecutablesPath,
                OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(inputFile.FullName);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0");
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-sn");
            startInfo.ArgumentList.Add("-dn");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("16000");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("wav");
            startInfo.ArgumentList.Add(tempOutputPath);

            try
            {
                using Process process = new() { StartInfo = startInfo };
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to start FFmpeg: {ffmpegPath}");
                }

                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                using CancellationTokenRegistration registration = token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Best-effort cancellation. WaitForExitAsync below remains authoritative.
                    }
                });

                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(tempOutputPath);
                    throw;
                }

                string stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg failed to normalize '{inputFile.FullName}' (exit code {process.ExitCode}). " +
                        $"FFmpeg output:\n{Tail(stderr, 8000)}");
                }

                FileInfo output = new(tempOutputPath);
                output.Refresh();
                if (!output.Exists || output.Length <= 44)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg reported success but did not create a valid WAV file at '{tempOutputPath}'. " +
                        $"FFmpeg output:\n{Tail(stderr, 8000)}");
                }

                _logger.Information(
                    "Temporary WAV prepared successfully: {outputPath} ({sizeBytes} bytes)",
                    output.FullName,
                    output.Length);
                return output;
            }
            catch
            {
                TryDelete(tempOutputPath);
                throw;
            }
        }

        private static string Tail(string value, int maxCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters)
            {
                return value;
            }

            return "..." + value[^maxCharacters..];
        }

        private static void EnsureExecutable(string path)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path))
            {
                return;
            }

            UnixFileMode mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }

        private static List<GgmlType> ResolveFallbackModels(GgmlType primary, string specification)
        {
            if (string.Equals(specification?.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            IEnumerable<GgmlType> candidates;
            if (string.IsNullOrWhiteSpace(specification) || string.Equals(specification.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                candidates = primary switch
                {
                    GgmlType.LargeV3Turbo => [GgmlType.LargeV3, GgmlType.LargeV2],
                    GgmlType.LargeV3 => [GgmlType.LargeV3Turbo, GgmlType.LargeV2],
                    GgmlType.LargeV2 => [GgmlType.LargeV3, GgmlType.LargeV3Turbo],
                    GgmlType.LargeV1 => [GgmlType.LargeV3, GgmlType.LargeV3Turbo],
                    _ => [GgmlType.LargeV3Turbo, GgmlType.LargeV3]
                };
            }
            else
            {
                List<GgmlType> parsed = [];
                foreach (string part in specification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse(part, ignoreCase: true, out GgmlType model))
                    {
                        throw new ArgumentException($"Unknown fallback Whisper model '{part}'.");
                    }
                    parsed.Add(model);
                }
                candidates = parsed;
            }

            return candidates.Where(m => m != primary).Distinct().ToList();
        }

        private static string BuildFingerprint(AppOptions options, IReadOnlyList<GgmlType> fallbackModels, bool usedVad)
        {
            string raw = string.Join("|",
                "robust-v9-isolated-native-worker",
                options.Model,
                string.Join(",", fallbackModels),
                options.Language,
                options.LockDetectedLanguage,
                usedVad,
                options.ChunkSeconds,
                options.MaxChunkSeconds,
                options.EntropyThreshold,
                options.Temperature,
                options.TemperatureIncrement,
                options.GlitchThreshold,
                options.MaxRecoveryDepth,
                options.MinRecoverySplitSeconds,
                options.VadThreshold,
                options.VadMinSpeechMs,
                options.VadMinSilenceMs,
                options.VadSpeechPaddingMs,
                options.VadEdgePaddingMs);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }

        private static TranscriptionCheckpoint NewCheckpoint(FileInfo inputFile, string fingerprint) => new()
        {
            InputPath = inputFile.FullName,
            InputLength = inputFile.Length,
            InputLastWriteUtc = inputFile.LastWriteTimeUtc,
            Fingerprint = fingerprint,
            UpdatedUtc = DateTime.UtcNow
        };

        /// <summary>
        /// A compatible, fully covered terminal result needs neither audio conversion nor
        /// a model/FFmpeg/CUDA preflight. File existence alone is never a completion test.
        /// </summary>
        private async Task<FileInfo?> TryReuseCompletedRunAsync(
            FileInfo inputFile,
            AppOptions options,
            IReadOnlyList<GgmlType> fallbackModels,
            CancellationToken token)
        {
            if (!options.Resume || options.RetryReview || options.RevalidateCheckpoint)
                return null;

            string checkpointPath = BuildModelScopedPath(inputFile, options.Model, ".transcription.checkpoint.json");
            string legacyCheckpointPath = BuildSidecarPath(inputFile, ".transcription.checkpoint.json");
            string sourcePath = File.Exists(checkpointPath) ? checkpointPath : legacyCheckpointPath;
            bool legacy = !string.Equals(sourcePath, checkpointPath, StringComparison.Ordinal);
            string reportPath = legacy
                ? BuildSidecarPath(inputFile, ".transcription.json")
                : BuildModelScopedPath(inputFile, options.Model, ".transcription.json");
            if (!File.Exists(sourcePath) || !File.Exists(reportPath))
                return null;

            TranscriptionCheckpoint? checkpoint;
            TranscriptionRunReport? report;
            try
            {
                checkpoint = JsonSerializer.Deserialize<TranscriptionCheckpoint>(
                    await File.ReadAllTextAsync(sourcePath, token), JsonOptions);
                report = JsonSerializer.Deserialize<TranscriptionRunReport>(
                    await File.ReadAllTextAsync(reportPath, token), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.Warning("Completed-result cache could not be read ({message}); checking the regular checkpoint path", ex.Message);
                return null;
            }

            if (checkpoint is null || report is null ||
                !IsSupportedCheckpointSchema(checkpoint.SchemaVersion) ||
                !IsSupportedCheckpointSchema(report.SchemaVersion) ||
                !SameInputPath(report.InputPath, inputFile.FullName) ||
                !string.Equals(report.PrimaryModel, options.Model.ToString(), StringComparison.Ordinal) ||
                report.StartedUtc == default || report.CompletedUtc < report.StartedUtc ||
                !CheckpointReusePolicy.CoversWholeAudio(report.Chunks, report.AudioDuration))
                return null;

            string fingerprint = BuildFingerprint(options, fallbackModels, options.UseVad && report.UsedVad);
            if (!CheckpointMatchesInput(checkpoint, inputFile, fingerprint) ||
                checkpoint.CompletedChunks is null || checkpoint.CompletedChunks.Count == 0 ||
                checkpoint.CompletedChunks.Count > report.Chunks.Count ||
                checkpoint.CompletedChunks.Any(c => !CheckpointReusePolicy.IsTerminal(c)))
                return null;

            // Every existing checkpoint entry must agree exactly with the last completed
            // report. Do not roll back results from a newer, interrupted explicit repair.
            var seen = new HashSet<(TimeSpan Start, TimeSpan End)>();
            foreach (ChunkTranscriptionResult cached in checkpoint.CompletedChunks)
            {
                if (!seen.Add((cached.Chunk.Start, cached.Chunk.End)))
                    return null;
                ChunkTranscriptionResult? saved = report.Chunks.FirstOrDefault(c => SameChunk(c.Chunk, cached.Chunk));
                if (saved is null || JsonSerializer.Serialize(cached, JsonOptions) != JsonSerializer.Serialize(saved, JsonOptions))
                    return null;
            }

            bool legacyReportIdentity = report.InputLength is null && report.InputLastWriteUtc is null &&
                                        string.IsNullOrWhiteSpace(report.Fingerprint);
            if (!legacyReportIdentity)
            {
                if (report.InputLength != inputFile.Length || report.InputLastWriteUtc != inputFile.LastWriteTimeUtc ||
                    !string.Equals(report.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return null;
            }
            else
            {
                // v15 reports lack input size/mtime/options identity. Accept them only with
                // a corroborating checkpoint and unchanged input predating that report.
                // The 2-second allowance covers final checkpoint/report write ordering.
                if (inputFile.LastWriteTimeUtc > report.StartedUtc ||
                    report.CompletedUtc > checkpoint.UpdatedUtc.AddSeconds(2))
                    return null;
            }

            int recovered = report.Chunks.Count - checkpoint.CompletedChunks.Count;
            bool flagsChanged = false;
            foreach (ChunkTranscriptionResult result in report.Chunks)
                flagsChanged |= CheckpointReusePolicy.PropagateReviewFlags(result);
            bool review = report.Chunks.Any(CheckpointReusePolicy.NeedsReview);
            flagsChanged |= report.NeedsReview != review;
            report.NeedsReview = review;
            report.InputLength = inputFile.Length;
            report.InputLastWriteUtc = inputFile.LastWriteTimeUtc;
            report.Fingerprint = fingerprint;

            if (recovered > 0 && !legacy)
            {
                // v14/v15 may already have deleted terminal entries during an interrupted
                // revalidation run. Their final report can still retain those exact results.
                string backup = checkpointPath + ".pre-v16.bak";
                if (!File.Exists(backup))
                    File.Copy(checkpointPath, backup, overwrite: false);
            }
            if (legacy || recovered > 0 || flagsChanged || checkpoint.SchemaVersion == "9")
            {
                checkpoint.CompletedChunks = report.Chunks.OrderBy(c => c.Chunk.Start).ToList();
                checkpoint.SchemaVersion = "10";
                checkpoint.UpdatedUtc = DateTime.UtcNow;
                await SaveJsonAtomicAsync(checkpointPath, checkpoint, token);
            }
            if (recovered > 0)
                _logger.Information("Recovered {count} terminal checkpoint entries from the matching completed report; no inference is required", recovered);
            if (legacy)
                _logger.Information("Migrated completed legacy result into the {model} output namespace; legacy files were left untouched", options.Model);

            string textPath = BuildModelScopedPath(inputFile, options.Model, ".txt");
            bool outputsPresent = new[] { ".txt", ".srt", ".vtt", ".transcription.json", ".transcription.log" }
                .All(suffix => File.Exists(BuildModelScopedPath(inputFile, options.Model, suffix)));
            if (review && !File.Exists(BuildModelScopedPath(inputFile, options.Model, ".transcription.review.txt")))
                outputsPresent = false;

            if (legacy || !outputsPresent || flagsChanged)
            {
                _logger.Information("Restoring output files from the completed report without retranscription");
                await WriteOutputsAsync(inputFile, options.Model, report, token);
            }
            else if (legacyReportIdentity)
            {
                // Enrich old reports once for strict identity checks on future fast resumes.
                // Transcript/subtitle contents and the original run times are not changed.
                await SaveJsonAtomicAsync(reportPath, report, token);
            }

            _logger.Information("Already processed: {reused}/{total} chunks reused; 0 Whisper attempts; FFmpeg/model preflight skipped. Output: {path}",
                report.Chunks.Count, report.Chunks.Count, textPath);
            if (review)
                _logger.Warning("Cached result is completed-with-review ({count} intervals). No automatic retry; use --retry-review explicitly. Review file: {path}",
                    report.Chunks.Count(CheckpointReusePolicy.NeedsReview),
                    BuildModelScopedPath(inputFile, options.Model, ".transcription.review.txt"));
            return new FileInfo(textPath);
        }

        private static bool IsSupportedCheckpointSchema(string? schema) => schema is "9" or "10";

        private static bool SameInputPath(string? first, string second) =>
            string.Equals(first, second, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static bool CheckpointMatchesInput(TranscriptionCheckpoint checkpoint, FileInfo inputFile, string fingerprint) =>
            SameInputPath(checkpoint.InputPath, inputFile.FullName) &&
            checkpoint.InputLength == inputFile.Length &&
            checkpoint.InputLastWriteUtc == inputFile.LastWriteTimeUtc &&
            string.Equals(checkpoint.Fingerprint, fingerprint, StringComparison.Ordinal);

        private async Task<TranscriptionCheckpoint> LoadCheckpointAsync(
            string path,
            string legacyPath,
            FileInfo inputFile,
            string fingerprint,
            AppOptions options,
            CancellationToken token)
        {
            string? sourcePath = File.Exists(path) ? path : File.Exists(legacyPath) ? legacyPath : null;
            if (sourcePath is null)
                return NewCheckpoint(inputFile, fingerprint);
            bool usingLegacyPath = !string.Equals(sourcePath, path, StringComparison.Ordinal);

            TranscriptionCheckpoint? checkpoint;
            try
            {
                checkpoint = JsonSerializer.Deserialize<TranscriptionCheckpoint>(
                    await File.ReadAllTextAsync(sourcePath, token), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not read transcription checkpoint; starting a fresh run");
                return NewCheckpoint(inputFile, fingerprint);
            }
            if (checkpoint is null || !IsSupportedCheckpointSchema(checkpoint.SchemaVersion) ||
                checkpoint.CompletedChunks is null || !CheckpointMatchesInput(checkpoint, inputFile, fingerprint))
            {
                _logger.Information("Checkpoint does not match this input/options or has an unsupported schema; it will not be reused");
                return NewCheckpoint(inputFile, fingerprint);
            }

            int originalCount = checkpoint.CompletedChunks.Count;
            bool changed = usingLegacyPath || checkpoint.SchemaVersion == "9";
            _dynamicHallucinationSegments.UnionWith(checkpoint.KnownHallucinationSegments ?? []);
            // Invalid infrastructure entries must not enter the text-pattern detector.
            var terminal = checkpoint.CompletedChunks.Where(CheckpointReusePolicy.IsTerminal).ToList();
            HashSet<string> recurring = FindRecurringCrossChunkSegments(terminal);
            _dynamicHallucinationSegments.UnionWith(recurring);
            if (recurring.Count > 0)
                _logger.Information("Recorded {count} recurring text patterns for any new attempts. Ordinary resume does not invalidate completed outcomes", recurring.Count);

            List<ChunkTranscriptionResult> reusable = [];
            List<string> invalidated = [];
            foreach (ChunkTranscriptionResult result in checkpoint.CompletedChunks)
            {
                string? reason = null;
                if (!CheckpointReusePolicy.IsTerminal(result))
                {
                    reason = "saved entry is incomplete, malformed, or contains an infrastructure failure";
                }
                else
                {
                    changed |= CheckpointReusePolicy.PropagateReviewFlags(result);
                    if (options.RevalidateCheckpoint)
                    {
                        bool passed = TryRevalidateCachedChunk(result, options.GlitchThreshold,
                            _dynamicHallucinationSegments, out QualityAssessment reassessed);
                        result.Quality = reassessed;
                        changed = true;
                        if (!passed)
                            reason = "explicit quality revalidation: " + string.Join("; ", reassessed.Reasons);
                    }
                    if (reason is null && options.RetryReview && CheckpointReusePolicy.NeedsReview(result))
                        reason = "explicit --retry-review requested";
                }
                if (reason is null)
                {
                    reusable.Add(result);
                }
                else
                {
                    string id = result?.Chunk?.Id ?? "<malformed>";
                    invalidated.Add(id);
                    _logger.Warning("Cached chunk {chunkId} scheduled for processing: {reason}", id, reason);
                }
            }

            checkpoint.CompletedChunks = reusable.OrderBy(c => c.Chunk.Start).ToList();
            checkpoint.SchemaVersion = "10";
            List<string> patterns = _dynamicHallucinationSegments.OrderBy(s => s).ToList();
            changed |= !(checkpoint.KnownHallucinationSegments ?? []).SequenceEqual(patterns);
            checkpoint.KnownHallucinationSegments = patterns;
            if (changed || invalidated.Count > 0)
            {
                checkpoint.UpdatedUtc = DateTime.UtcNow;
                await SaveJsonAtomicAsync(path, checkpoint, token);
            }
            if (usingLegacyPath)
                _logger.Information("Compatible legacy checkpoint copied to {path}; legacy file left untouched", path);
            _logger.Information("Checkpoint retained {retained}/{original} completed chunks, including {review} completed-with-review outcomes; {retry} scheduled for processing",
                reusable.Count, originalCount, reusable.Count(CheckpointReusePolicy.NeedsReview), invalidated.Count);
            return checkpoint;
        }

        private static ChunkTranscriptionResult? FindCachedChunk(TranscriptionCheckpoint checkpoint, AudioChunk chunk) =>
            checkpoint.CompletedChunks.FirstOrDefault(c =>
                CheckpointReusePolicy.IsTerminal(c) && SameChunk(c.Chunk, chunk));

        private static bool IsReusableCachedChunk(ChunkTranscriptionResult result) =>
            CheckpointReusePolicy.IsTerminal(result);

        private static bool TryRevalidateCachedChunk(
            ChunkTranscriptionResult result,
            double glitchThreshold,
            IReadOnlySet<string> recurringSegments,
            out QualityAssessment reassessed)
        {
            reassessed = TranscriptionQualityAnalyzer.Analyze(
                result.Segments, result.Chunk.Duration, result.Chunk.ExpectedSpeech, glitchThreshold);
            ApplyDynamicHallucinationPenalty(reassessed, result.Segments, recurringSegments);
            bool childrenPassed = true;
            foreach (ChunkTranscriptionResult child in result.SubChunks)
            {
                bool passed = TryRevalidateCachedChunk(child, glitchThreshold, recurringSegments, out QualityAssessment childQuality);
                child.Quality = childQuality;
                child.NeedsReview |= !passed;
                childrenPassed &= passed;
            }
            result.NeedsReview |= reassessed.Suspicious || !childrenPassed;
            return CheckpointReusePolicy.IsTerminal(result) && !reassessed.Suspicious && childrenPassed;
        }


        private static void ApplyDynamicHallucinationPenalty(
            QualityAssessment quality,
            IReadOnlyList<TranscriptSegment> segments,
            IReadOnlySet<string> recurringSegments)
        {
            if (recurringSegments.Count == 0)
            {
                return;
            }

            string? matched = segments
                .Select(s => TranscriptionQualityAnalyzer.NormalizeForComparison(s.Text))
                .FirstOrDefault(s => recurringSegments.Contains(s));
            if (matched is null)
            {
                return;
            }

            quality.Score = Math.Max(quality.Score, 0.85);
            quality.Suspicious = true;
            string reason = $"segment matches a recurring cross-chunk hallucination pattern: '{TruncateForLog(matched, 80)}'";
            if (!quality.Reasons.Contains(reason, StringComparer.Ordinal))
            {
                quality.Reasons.Add(reason);
            }
        }

        private static HashSet<string> FindRecurringCrossChunkSegments(
            IReadOnlyList<ChunkTranscriptionResult> results)
        {
            Dictionary<string, RecurringSegmentStats> stats = new(StringComparer.Ordinal);

            foreach (ChunkTranscriptionResult result in results)
            {
                int chunkWords = TranscriptionQualityAnalyzer.CountWords(
                    string.Join(' ', result.Segments.Select(s => s.Text)));

                foreach (TranscriptSegment segment in result.Segments)
                {
                    string normalized = TranscriptionQualityAnalyzer.NormalizeForComparison(segment.Text);
                    int segmentWords = TranscriptionQualityAnalyzer.CountWords(normalized);
                    if (segmentWords < 3 || segmentWords > 12 || normalized.Length < 8)
                    {
                        continue;
                    }

                    if (!stats.TryGetValue(normalized, out RecurringSegmentStats? item))
                    {
                        item = new RecurringSegmentStats();
                        stats[normalized] = item;
                    }

                    item.ChunkIds.Add(result.Chunk.Id);
                    if (chunkWords <= 25 || (chunkWords > 0 && (double)segmentWords / chunkWords >= 0.20))
                    {
                        item.DominantOccurrences++;
                    }
                }
            }

            return stats
                .Where(kvp => kvp.Value.ChunkIds.Count >= 3 && kvp.Value.DominantOccurrences >= 2)
                .Select(kvp => kvp.Key)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool ContainsAnyRecurringSegment(
            IReadOnlyList<TranscriptSegment> segments,
            IReadOnlySet<string> recurringSegments)
        {
            if (recurringSegments.Count == 0)
            {
                return false;
            }

            return segments.Any(s => recurringSegments.Contains(
                TranscriptionQualityAnalyzer.NormalizeForComparison(s.Text)));
        }

        private static string TruncateForLog(string text, int maxLength)
        {
            if (text.Length <= maxLength)
            {
                return text;
            }
            return text[..Math.Max(1, maxLength - 1)] + "…";
        }

        private sealed class RecurringSegmentStats
        {
            public HashSet<string> ChunkIds { get; } = new(StringComparer.Ordinal);
            public int DominantOccurrences { get; set; }
        }

        private static bool ContainsAttemptError(ChunkTranscriptionResult result)
        {
            if (result.Attempts.Any(a => !string.IsNullOrWhiteSpace(a.Error)))
            {
                return true;
            }

            return result.SubChunks.Any(ContainsAttemptError);
        }

        private static bool SameChunk(AudioChunk a, AudioChunk b)
        {
            return Math.Abs((a.Start - b.Start).TotalMilliseconds) < 5 &&
                   Math.Abs((a.End - b.End).TotalMilliseconds) < 5;
        }

        private static (List<AudioChunk> ResumedPlan, TimeSpan PreservedUntil) PreserveCheckpointPrefix(
            IReadOnlyList<AudioChunk> planned,
            IReadOnlyList<ChunkTranscriptionResult> completed,
            TimeSpan audioDuration,
            IReadOnlyList<SpeechRegion> speechRegions)
        {
            const double toleranceMs = 5;
            TimeSpan cursor = TimeSpan.Zero;
            List<AudioChunk> prefix = [];

            foreach (ChunkTranscriptionResult result in completed.OrderBy(c => c.Chunk.Start))
            {
                AudioChunk chunk = result.Chunk;
                if (Math.Abs((chunk.Start - cursor).TotalMilliseconds) > toleranceMs || chunk.End <= chunk.Start)
                {
                    break;
                }

                prefix.Add(new AudioChunk
                {
                    Id = string.Empty,
                    Start = chunk.Start,
                    End = chunk.End,
                    ExpectedSpeech = chunk.ExpectedSpeech
                });
                cursor = chunk.End;
            }

            if (prefix.Count == 0)
            {
                List<AudioChunk> unchanged = planned.Select(CloneChunk).ToList();
                for (int i = 0; i < unchanged.Count; i++) unchanged[i].Id = (i + 1).ToString("D4");
                return (unchanged, TimeSpan.Zero);
            }

            if (cursor >= audioDuration)
            {
                for (int i = 0; i < prefix.Count; i++) prefix[i].Id = (i + 1).ToString("D4");
                return (prefix, cursor);
            }

            // Keep the old completed boundaries exactly, then use the new planner only for
            // future audio. Ignore a planned boundary that would create a tiny first suffix.
            List<AudioChunk> resultPlan = prefix;
            TimeSpan suffixStart = cursor;
            TimeSpan minimumUseful = TimeSpan.FromSeconds(20);

            foreach (TimeSpan end in planned
                         .Select(c => c.End)
                         .Where(end => end > suffixStart)
                         .Distinct()
                         .OrderBy(end => end))
            {
                if (end < audioDuration && end - suffixStart < minimumUseful)
                {
                    continue;
                }

                resultPlan.Add(new AudioChunk
                {
                    Id = string.Empty,
                    Start = suffixStart,
                    End = end,
                    ExpectedSpeech = speechRegions.Count > 0 && HasSpeechOverlap(speechRegions, suffixStart, end)
                });
                suffixStart = end;
            }

            if (suffixStart < audioDuration)
            {
                resultPlan.Add(new AudioChunk
                {
                    Id = string.Empty,
                    Start = suffixStart,
                    End = audioDuration,
                    ExpectedSpeech = speechRegions.Count > 0 && HasSpeechOverlap(speechRegions, suffixStart, audioDuration)
                });
            }

            for (int i = 0; i < resultPlan.Count; i++)
            {
                resultPlan[i].Id = (i + 1).ToString("D4");
            }

            return (resultPlan, cursor);
        }

        private static AudioChunk CloneChunk(AudioChunk chunk) => new()
        {
            Id = chunk.Id,
            Start = chunk.Start,
            End = chunk.End,
            ExpectedSpeech = chunk.ExpectedSpeech
        };

        private static string? DetectLanguageFromCompletedChunks(IEnumerable<ChunkTranscriptionResult> results)
        {
            foreach (ChunkTranscriptionResult result in results.OrderBy(r => r.Chunk.Start))
            {
                string? language = DetectLanguageFromResult(result);
                if (!string.IsNullOrWhiteSpace(language))
                {
                    return language;
                }
            }
            return null;
        }

        private static string? DetectLanguageFromResult(ChunkTranscriptionResult result)
        {
            if (result.NeedsReview)
            {
                return null;
            }

            return result.Segments
                .Select(s => s.Language)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !IsAutoLanguage(l))
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        private static bool IsAutoLanguage(string? language) =>
            string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

        private static bool SegmentBelongsToCoverage(TranscriptSegment segment, AudioChunk coverage, TimeSpan audioDuration)
        {
            TimeSpan midpoint = segment.Start + TimeSpan.FromTicks((segment.End - segment.Start).Ticks / 2);
            bool isLast = Math.Abs((coverage.End - audioDuration).TotalMilliseconds) < 5;
            return midpoint >= coverage.Start && (midpoint < coverage.End || (isLast && midpoint <= coverage.End));
        }

        private static bool HasSpeechOverlap(IReadOnlyList<SpeechRegion> speechRegions, TimeSpan start, TimeSpan end)
        {
            foreach (SpeechRegion region in speechRegions)
            {
                if (region.End <= start)
                {
                    continue;
                }
                if (region.Start >= end)
                {
                    break;
                }
                return true;
            }
            return false;
        }

        private static ChunkTranscriptionResult CreateAcceptedChunkResult(
            AudioChunk chunk,
            AttemptCandidate candidate,
            List<AttemptDiagnostic> diagnostics,
            bool needsReview)
        {
            return new ChunkTranscriptionResult
            {
                Chunk = chunk,
                SelectedModel = candidate.Profile.Model.ToString(),
                SelectedStrategy = candidate.Profile.Strategy,
                NeedsReview = needsReview,
                Quality = candidate.Quality,
                Segments = candidate.Segments,
                Attempts = diagnostics
            };
        }

        private static async Task WriteTextAtomicAsync(string path, string text, CancellationToken token)
        {
            string temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, text, Encoding.UTF8, token);
            File.Move(temp, path, overwrite: true);
        }

        private static async Task SaveJsonAtomicAsync<T>(string path, T value, CancellationToken token)
        {
            string temp = path + ".tmp";
            string json = JsonSerializer.Serialize(value, JsonOptions);
            await File.WriteAllTextAsync(temp, json, Encoding.UTF8, token);
            File.Move(temp, path, overwrite: true);
        }

        private static string BuildModelScopedPath(FileInfo inputFile, GgmlType primaryModel, string suffix)
        {
            string directory = inputFile.DirectoryName ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(inputFile.Name);
            string modelTag = SanitizeFileNamePart(primaryModel.ToString());
            return Path.Combine(directory, $"{stem}.{modelTag}{suffix}");
        }

        private static string SanitizeFileNamePart(string value)
        {
            HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
            StringBuilder sb = new(value.Length);
            foreach (char c in value)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }

            string sanitized = sb.ToString().Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(sanitized) ? "model" : sanitized;
        }

        private static string BuildSidecarPath(FileInfo inputFile, string suffix)
        {
            string directory = inputFile.DirectoryName ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(inputFile.Name);
            return Path.Combine(directory, stem + suffix);
        }

        private static string FormatSubtitleTime(TimeSpan value, char millisecondSeparator)
        {
            int hours = (int)value.TotalHours;
            return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}{millisecondSeparator}{value.Milliseconds:000}";
        }

        private static string FormatClock(TimeSpan value)
        {
            int hours = (int)value.TotalHours;
            return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temporary/obsolete sidecars are non-critical.
            }
        }

        private sealed record AttemptProfile(
            GgmlType Model,
            string Strategy,
            bool NoContext,
            double PreRollSeconds,
            double PostRollSeconds);

        private sealed class AttemptCandidate
        {
            public AttemptProfile Profile { get; }
            public List<TranscriptSegment> Segments { get; }
            public QualityAssessment Quality { get; }
            public AttemptDiagnostic Diagnostic { get; }

            public AttemptCandidate(
                AttemptProfile profile,
                List<TranscriptSegment> segments,
                QualityAssessment quality,
                AttemptDiagnostic diagnostic)
            {
                Profile = profile;
                Segments = segments;
                Quality = quality;
                Diagnostic = diagnostic;
            }

            public static AttemptCandidate Failed(string reason, AudioChunk chunk, double threshold)
            {
                var profile = new AttemptProfile(GgmlType.LargeV3Turbo, "failed", true, 0, 0);
                var quality = TranscriptionQualityAnalyzer.Analyze([], chunk.Duration, chunk.ExpectedSpeech, threshold);
                quality.Score = 1;
                quality.Suspicious = true;
                quality.Reasons.Add(reason);
                return new AttemptCandidate(profile, [], quality, new AttemptDiagnostic
                {
                    Model = "none",
                    Strategy = "failed",
                    Error = reason,
                    Quality = quality
                });
            }
        }

        private sealed class ModelFileCache
        {
            private readonly Func<GgmlType, CancellationToken, Task<FileInfo>> _resolver;
            private readonly Dictionary<GgmlType, FileInfo> _files = [];

            public ModelFileCache(Func<GgmlType, CancellationToken, Task<FileInfo>> resolver)
            {
                _resolver = resolver;
            }

            public async Task<FileInfo> GetAsync(GgmlType model, CancellationToken token)
            {
                if (_files.TryGetValue(model, out FileInfo? file) && file.Exists)
                {
                    return file;
                }

                FileInfo resolved = await _resolver(model, token);
                token.ThrowIfCancellationRequested();
                resolved.Refresh();
                if (!resolved.Exists || resolved.Length == 0)
                {
                    throw new FileNotFoundException($"Whisper model '{model}' was not resolved to a valid file.", resolved.FullName);
                }
                _files[model] = resolved;
                return resolved;
            }
        }
    }
}
