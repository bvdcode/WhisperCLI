using NAudio.Wave;
using Serilog;
using System.Diagnostics;
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
            WriteIndented = true
        };

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
            if (_modelResolver is null)
            {
                throw new InvalidOperationException("Robust transcription requires a model resolver.");
            }

            await CheckFfmpegAsync(token);
            FileInfo normalized = await ConvertToWaveFileAsync(inputFile, token);
            DateTime startedUtc = DateTime.UtcNow;

            try
            {
                TimeSpan audioDuration = WaveChunkReader.GetDuration(normalized.FullName);
                _logger.Information("Normalized audio duration: {duration}", FormatClock(audioDuration));

                List<VadSegmentData> speechRegions = [];
                bool usedVad = false;
                if (options.UseVad)
                {
                    try
                    {
                        speechRegions = await DetectSpeechRegionsAsync(normalized, options, token);
                        usedVad = true;
                        _logger.Information("Silero VAD detected {count} speech regions", speechRegions.Count);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Silero VAD failed; falling back to fixed-duration chunking");
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

                if (usedVad && chunks.Count == 0)
                {
                    _logger.Warning("VAD found no speech in the recording. Producing an empty transcript rather than hallucinating over silence.");
                }
                else
                {
                    _logger.Information("Prepared {count} independent transcription chunks (target {target}s, hard max {max}s)",
                        chunks.Count, options.ChunkSeconds, options.MaxChunkSeconds);
                }

                List<GgmlType> fallbackModels = ResolveFallbackModels(options.Model, options.FallbackModels);
                string fingerprint = BuildFingerprint(options, fallbackModels, usedVad);
                string checkpointPath = BuildSidecarPath(inputFile, ".transcription.checkpoint.json");
                TranscriptionCheckpoint checkpoint = options.Resume
                    ? await LoadCheckpointAsync(checkpointPath, inputFile, fingerprint, token)
                    : NewCheckpoint(inputFile, fingerprint);

                string partialTextPath = BuildSidecarPath(inputFile, ".partial.txt");
                string partialSrtPath = BuildSidecarPath(inputFile, ".partial.srt");
                string partialVttPath = BuildSidecarPath(inputFile, ".partial.vtt");
                _logger.Information("Incremental transcript: {partialTextPath}", partialTextPath);
                _logger.Information("Incremental subtitles: {partialSrtPath} and {partialVttPath}", partialSrtPath, partialVttPath);
                _logger.Information("Resume checkpoint: {checkpointPath}", checkpointPath);

                if (checkpoint.CompletedChunks.Count > 0)
                {
                    await WriteProgressOutputsAsync(inputFile, checkpoint.CompletedChunks, chunks.Count, CancellationToken.None);
                }

                // VAD uses the same native Whisper runtime and can be the first operation that
                // causes Whisper.net to select CUDA vs CPU. Validate only after restoring any
                // previous checkpoint so already-completed work remains immediately readable.
                WhisperRuntimeManager.ValidateLoadedRuntime(_logger);

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

                using ModelFactoryCache factories = new(_modelResolver, _logger, options);
                List<ChunkTranscriptionResult> completed = [];

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
                            factories,
                            lockedLanguage,
                            token);

                        checkpoint.CompletedChunks.RemoveAll(c => SameChunk(c.Chunk, chunk));
                        checkpoint.CompletedChunks.Add(result);
                        checkpoint.CompletedChunks = checkpoint.CompletedChunks
                            .OrderBy(c => c.Chunk.Start)
                            .ToList();
                        checkpoint.UpdatedUtc = DateTime.UtcNow;
                        await SaveJsonAtomicAsync(checkpointPath, checkpoint, CancellationToken.None);
                    }

                    completed.Add(result);
                    await WriteProgressOutputsAsync(inputFile, completed, chunks.Count, CancellationToken.None);

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

                var report = new TranscriptionRunReport
                {
                    InputPath = inputFile.FullName,
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

                return await WriteOutputsAsync(inputFile, report, token);
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
            IReadOnlyList<VadSegmentData> speechRegions,
            ModelFactoryCache factories,
            string? lockedLanguage,
            CancellationToken token)
        {
            if (speechRegions.Count > 0 && !chunk.ExpectedSpeech)
            {
                _logger.Debug("Chunk {chunkId} {start}-{end}: VAD indicates silence; skipping Whisper",
                    chunk.Id, FormatClock(chunk.Start), FormatClock(chunk.End));
                return CreateVadSilenceResult(chunk);
            }

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
                    factories,
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
                        speechRegions, factories, lockedLanguage, token);
                    ChunkTranscriptionResult rightResult = await TranscribeChunkWithRecoveryAsync(
                        normalizedWavPath, audioDuration, right, depth + 1, options, fallbackModels,
                        speechRegions, factories, lockedLanguage, token);

                    List<TranscriptSegment> merged = leftResult.Segments
                        .Concat(rightResult.Segments)
                        .OrderBy(s => s.Start)
                        .ToList();
                    QualityAssessment mergedQuality = TranscriptionQualityAnalyzer.Analyze(
                        merged, chunk.Duration, chunk.ExpectedSpeech, options.GlitchThreshold);

                    return new ChunkTranscriptionResult
                    {
                        Chunk = chunk,
                        SelectedModel = "multiple",
                        SelectedStrategy = "recursive-split-recovery",
                        NeedsReview = leftResult.NeedsReview || rightResult.NeedsReview,
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
            ModelFactoryCache factories,
            string? lockedLanguage,
            CancellationToken token)
        {
            TimeSpan audioStart = Max(TimeSpan.Zero, coverage.Start - TimeSpan.FromSeconds(profile.PreRollSeconds));
            TimeSpan audioEnd = Min(audioDuration, coverage.End + TimeSpan.FromSeconds(profile.PostRollSeconds));
            Stopwatch sw = Stopwatch.StartNew();
            List<TranscriptSegment> rawSegments = [];
            bool liveLoopAborted = false;

            var diagnostic = new AttemptDiagnostic
            {
                Model = profile.Model.ToString(),
                Strategy = profile.Strategy,
                AudioStart = audioStart,
                AudioEnd = audioEnd
            };

            try
            {
                WhisperFactory factory = await factories.GetAsync(profile.Model, token);
                var builder = factory.CreateBuilder()
                    .WithLanguage(string.IsNullOrWhiteSpace(lockedLanguage) ? options.Language : lockedLanguage)
                    .WithMaxLastTextTokens(options.MaxContextTokens)
                    .WithEntropyThreshold(options.EntropyThreshold)
                    .WithTemperature(options.Temperature)
                    .WithTemperatureInc(options.TemperatureIncrement);

                if (profile.NoContext)
                {
                    builder.WithNoContext();
                }

                await using WhisperProcessor processor = builder.Build();
                using MemoryStream audio = WaveChunkReader.ReadChunk(normalizedWavPath, audioStart, audioEnd);
                using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                try
                {
                    await foreach (var result in processor.ProcessAsync(audio, attemptCts.Token))
                    {
                        TranscriptSegment segment = new()
                        {
                            Start = audioStart + result.Start,
                            End = audioStart + result.End,
                            Text = result.Text,
                            Language = result.Language
                        };
                        rawSegments.Add(segment);

                        _logger.Debug("Chunk {chunkId} {model}/{strategy}: {start}->{end}: {text}",
                            coverage.Id, profile.Model, profile.Strategy,
                            FormatClock(segment.Start), FormatClock(segment.End), segment.Text);

                        if (TranscriptionQualityAnalyzer.HasObviousLiveLoop(rawSegments))
                        {
                            liveLoopAborted = true;
                            diagnostic.AbortedForLiveLoop = true;
                            attemptCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (liveLoopAborted && !token.IsCancellationRequested)
                {
                    // Expected: this attempt was deliberately stopped because it entered a repetition loop.
                }

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
                    liveLoopAborted);

                sw.Stop();
                diagnostic.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                diagnostic.Quality = quality;
                return new AttemptCandidate(profile, selectedSegments, quality, diagnostic);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                diagnostic.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                diagnostic.Error = ex.ToString();
                diagnostic.Quality = new QualityAssessment
                {
                    Score = 1.0,
                    Suspicious = true,
                    Reasons = ["attempt failed with an exception: " + ex.Message]
                };
                return new AttemptCandidate(profile, [], diagnostic.Quality, diagnostic);
            }
        }

        private async Task<List<VadSegmentData>> DetectSpeechRegionsAsync(FileInfo normalizedWav, AppOptions options, CancellationToken token)
        {
            string vadModelPath = await GetVadModelPathAsync(token);
            using WhisperVadFactory vadFactory = WhisperVadFactory.FromPath(vadModelPath);
            await using WhisperVadProcessor vadProcessor = vadFactory.CreateBuilder()
                .WithThreshold(options.VadThreshold)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(options.VadMinSpeechMs))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(options.VadMinSilenceMs))
                .WithMaxSpeechDuration(TimeSpan.FromSeconds(options.MaxChunkSeconds))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(options.VadSpeechPaddingMs))
                .Build();

            await using FileStream stream = normalizedWav.OpenRead();
            IReadOnlyList<VadSegmentData> regions = await vadProcessor.DetectSpeechAsync(stream, token);
            return regions.ToList();
        }

        private async Task<string> GetVadModelPathAsync(CancellationToken token)
        {
            string modelDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI", "Models");
            Directory.CreateDirectory(modelDirectory);
            string path = Path.Combine(modelDirectory, "ggml-silero-v6.2.0.bin");
            if (File.Exists(path))
            {
                var existing = new FileInfo(path);
                if (existing.Length >= 800_000)
                {
                    return path;
                }

                _logger.Warning("Existing Silero VAD model looks incomplete and will be re-downloaded: {path}", path);
                TryDelete(path);
            }

            _logger.Information("Downloading Silero VAD model...");
            string tempPath = path + $".{Guid.NewGuid():N}.download";
            try
            {
                using Stream modelStream = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync();
                await using (FileStream writer = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await modelStream.CopyToAsync(writer, token);
                    await writer.FlushAsync(token);
                }
                File.Move(tempPath, path, overwrite: true);
                _logger.Information("Silero VAD model downloaded: {path}", path);
                return path;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private async Task WriteProgressOutputsAsync(
            FileInfo inputFile,
            IReadOnlyList<ChunkTranscriptionResult> completedChunks,
            int totalChunks,
            CancellationToken token)
        {
            List<TranscriptSegment> segments = completedChunks
                .SelectMany(FlattenSegments)
                .OrderBy(s => s.Start)
                .ToList();

            string textPath = BuildSidecarPath(inputFile, ".partial.txt");
            string srtPath = BuildSidecarPath(inputFile, ".partial.srt");
            string vttPath = BuildSidecarPath(inputFile, ".partial.vtt");

            await WriteTextAtomicAsync(textPath, AssembleText(segments), token);
            await WriteTextAtomicAsync(srtPath, BuildSrt(segments), token);
            await WriteTextAtomicAsync(vttPath, BuildVtt(segments), token);

            int completedTopLevel = completedChunks.Count;
            _logger.Information(
                "Progress saved: {completed}/{total} chunks -> {partialTextPath}",
                completedTopLevel, totalChunks, textPath);
        }

        private async Task<FileInfo> WriteOutputsAsync(FileInfo inputFile, TranscriptionRunReport report, CancellationToken token)
        {
            List<TranscriptSegment> segments = report.Chunks
                .SelectMany(FlattenSegments)
                .OrderBy(s => s.Start)
                .ToList();

            string textPath = Path.ChangeExtension(inputFile.FullName, ".txt");
            string srtPath = Path.ChangeExtension(inputFile.FullName, ".srt");
            string vttPath = Path.ChangeExtension(inputFile.FullName, ".vtt");
            string jsonPath = BuildSidecarPath(inputFile, ".transcription.json");
            string logPath = BuildSidecarPath(inputFile, ".transcription.log");
            string reviewPath = BuildSidecarPath(inputFile, ".transcription.review.txt");

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

            TryDelete(BuildSidecarPath(inputFile, ".partial.txt"));
            TryDelete(BuildSidecarPath(inputFile, ".partial.srt"));
            TryDelete(BuildSidecarPath(inputFile, ".partial.vtt"));

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
            sb.AppendLine($"VAD: {(report.UsedVad ? "Silero" : "fixed chunks")}, regions={report.VadSpeechRegionCount}");
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
            sb.AppendLine("The following intervals exhausted automatic recovery and should be checked manually:");
            sb.AppendLine();
            foreach (ChunkTranscriptionResult chunk in report.Chunks.Where(c => c.NeedsReview))
            {
                AppendReviewEntries(sb, chunk);
            }
            return sb.ToString();
        }

        private static void AppendReviewEntries(StringBuilder sb, ChunkTranscriptionResult chunk)
        {
            if (chunk.SubChunks.Count == 0 && chunk.NeedsReview)
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
            if (!File.Exists(path))
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
                "robust-v3",
                options.Model,
                string.Join(",", fallbackModels),
                options.Language,
                options.LockDetectedLanguage,
                usedVad,
                options.ChunkSeconds,
                options.MaxChunkSeconds,
                options.MaxContextTokens,
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

        private async Task<TranscriptionCheckpoint> LoadCheckpointAsync(
            string path,
            FileInfo inputFile,
            string fingerprint,
            CancellationToken token)
        {
            if (!File.Exists(path))
            {
                return NewCheckpoint(inputFile, fingerprint);
            }

            try
            {
                string json = await File.ReadAllTextAsync(path, token);
                TranscriptionCheckpoint? checkpoint = JsonSerializer.Deserialize<TranscriptionCheckpoint>(json, JsonOptions);
                bool matches = checkpoint is not null &&
                               checkpoint.SchemaVersion == "3" &&
                               checkpoint.InputLength == inputFile.Length &&
                               checkpoint.InputLastWriteUtc == inputFile.LastWriteTimeUtc &&
                               checkpoint.Fingerprint == fingerprint;

                if (matches)
                {
                    _logger.Information("Valid checkpoint found with {count} completed chunks", checkpoint!.CompletedChunks.Count);
                    return checkpoint!;
                }

                _logger.Information("Existing checkpoint does not match the current input/options and will not be reused");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Could not read transcription checkpoint; starting a fresh run");
            }

            return NewCheckpoint(inputFile, fingerprint);
        }

        private static ChunkTranscriptionResult? FindCachedChunk(TranscriptionCheckpoint checkpoint, AudioChunk chunk)
        {
            return checkpoint.CompletedChunks.FirstOrDefault(c => SameChunk(c.Chunk, chunk));
        }

        private static bool SameChunk(AudioChunk a, AudioChunk b)
        {
            return Math.Abs((a.Start - b.Start).TotalMilliseconds) < 5 &&
                   Math.Abs((a.End - b.End).TotalMilliseconds) < 5;
        }

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

        private static bool HasSpeechOverlap(IReadOnlyList<VadSegmentData> speechRegions, TimeSpan start, TimeSpan end)
        {
            foreach (VadSegmentData region in speechRegions)
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

        private static ChunkTranscriptionResult CreateVadSilenceResult(AudioChunk chunk) => new()
        {
            Chunk = chunk,
            SelectedModel = "none",
            SelectedStrategy = "vad-silence-skip",
            NeedsReview = false,
            Quality = new QualityAssessment
            {
                Score = 0,
                Suspicious = false,
                Reasons = ["Silero VAD found no speech in this interval; Whisper was not invoked."]
            },
            Segments = [],
            Attempts = []
        };

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

        private sealed class ModelFactoryCache : IDisposable
        {
            private readonly Func<GgmlType, CancellationToken, Task<FileInfo>> _resolver;
            private readonly ILogger _logger;
            private readonly AppOptions _options;
            private GgmlType? _loadedModel;
            private WhisperFactory? _factory;

            public ModelFactoryCache(
                Func<GgmlType, CancellationToken, Task<FileInfo>> resolver,
                ILogger logger,
                AppOptions options)
            {
                _resolver = resolver;
                _logger = logger;
                _options = options;
            }

            public async Task<WhisperFactory> GetAsync(GgmlType model, CancellationToken token)
            {
                if (_factory is not null && _loadedModel == model)
                {
                    return _factory;
                }

                // Resolve/download the next model file before releasing the currently loaded
                // model. Downloading does not consume GPU VRAM, and this avoids throwing away
                // a healthy primary model merely because a fallback download failed.
                FileInfo modelFile = await _resolver(model, token);
                token.ThrowIfCancellationRequested();

                if (_factory is not null)
                {
                    _logger.Information(
                        "Unloading Whisper model {previousModel} before switching to {model} (prevents multi-Large-model VRAM exhaustion)",
                        _loadedModel, model);
                    _factory.Dispose();
                    _factory = null;
                    _loadedModel = null;
                }

                _logger.Information("Loading Whisper model: {model}", model);
                _factory = WhisperFactory.FromPath(
                    modelFile.FullName,
                    WhisperRuntimeManager.CreateFactoryOptions(_options));
                WhisperRuntimeManager.ValidateLoadedRuntime(_logger);
                _loadedModel = model;
                return _factory;
            }

            public void Dispose()
            {
                _factory?.Dispose();
                _factory = null;
                _loadedModel = null;
            }
        }
    }
}
