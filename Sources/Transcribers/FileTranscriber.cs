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

                List<GgmlType> fallbackModels = ResolveFallbackModels(options.Model, options.FallbackModels);
                string fingerprint = BuildFingerprint(options, fallbackModels, usedVad);
                string checkpointPath = BuildSidecarPath(inputFile, ".transcription.checkpoint.json");
                TranscriptionCheckpoint checkpoint = options.Resume
                    ? await LoadCheckpointAsync(checkpointPath, inputFile, fingerprint, token)
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
                }

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

                // The managed boundary detector does not load Whisper. At this point the runtime may
                // still be unloaded; ValidateLoadedRuntime becomes authoritative after the first model factory is created.
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
            IReadOnlyList<SpeechRegion> speechRegions,
            ModelFactoryCache factories,
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
            catch (WhisperRuntimeUnavailableException)
            {
                // Runtime/backend selection is an infrastructure failure, not a bad transcript.
                // Retrying models, shifting boundaries, or recursively splitting audio cannot fix it,
                // so abort the run immediately instead of creating a retry storm.
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
            sb.AppendLine($"Boundary detection: {(report.UsedVad ? "managed energy/silence" : "fixed chunks")}, activity regions={report.VadSpeechRegionCount}");
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
                _factory = WhisperRuntimeManager.CreateFactory(modelFile.FullName, _options);
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
