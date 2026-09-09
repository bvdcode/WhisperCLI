using CommandLine;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.Logger;
using WhisperCLI.Transcribers;
using WhisperCLI.Transcribers.Robust;

namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Native whisper.cpp backends can terminate the process without a managed exception.
            // Robust file transcription therefore executes each inference attempt in an isolated
            // worker process. Keep this branch before CommandLineParser so the internal protocol
            // cannot collide with public CLI options.
            if (InternalWhisperWorker.IsWorkerInvocation(args))
            {
                int workerExitCode = await InternalWhisperWorker.RunAsync(args[1]);
                Environment.ExitCode = workerExitCode;
                return;
            }

            AppOptions? options = null;
            ParserResult<AppOptions> parseResult = Parser.Default.ParseArguments<AppOptions>(args);
            parseResult.WithParsed(parsed => options = parsed);
            if (options is null)
            {
                return;
            }

            try
            {
                ValidateOptions(options);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Invalid command-line options: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            Console.OutputEncoding = Encoding.UTF8;

            using CancellationTokenSource cts = new();

            using var logger = new LoggerConfiguration()
                .MinimumLevel.Is(options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            Log.Logger = logger;

            LogProvider.AddLogger((level, text) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    WhisperRuntimeManager.ObserveNativeLog(level.ToString(), text);
                    logger.Debug("[Whisper] [{level}] {text}", level.ToString().ToUpperInvariant(), text.Trim());
                }
            });

            int cancelPressCount = 0;
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                int press = Interlocked.Increment(ref cancelPressCount);
                if (press == 1)
                {
                    e.Cancel = true;
                    logger.Warning("Cancellation requested. Completed chunks are already saved. Press Ctrl+C again to terminate immediately.");
                    cts.Cancel();
                }
                else
                {
                    // Do not cancel the second SIGINT: let the OS terminate the process even
                    // if native Whisper code does not return from cooperative cancellation.
                    e.Cancel = false;
                }
            };
            Console.CancelKeyPress += cancelHandler;

            SingleInstanceLock? instanceLock = null;
            try
            {
                if (options.UseLockfile)
                {
                    instanceLock = SingleInstanceLock.TryAcquire(logger);
                    if (instanceLock is null)
                    {
                        logger.Warning("Another WhisperCLI instance appears to be running.");
                        await DelayBeforeExitAsync(options.DelaySeconds, cts.Token);
                        return;
                    }
                }

                FileInfo result;
                string osType = Environment.OSVersion.Platform.ToString();
                logger.Information("Operating System: {osType}", osType);
                logger.Information("WhisperCLI robust build: v10-nvidia-prime-vulkan");

                WhisperRuntimeManager.Configure(options, logger);

                if (string.IsNullOrWhiteSpace(options.InputFilePath))
                {
                    // Microphone recordings are short and keep the lightweight path.
                    FileInfo whisperModelInfo = await GetWhisperModelPathAsync(options.Model, logger, cts.Token);
                    using WhisperFactory microphoneFactory = WhisperRuntimeManager.CreateFactory(
                        whisperModelInfo.FullName, options);
                    WhisperRuntimeManager.ValidateLoadedRuntime(logger);
                    await using WhisperProcessor processor = CreateMicrophoneProcessor(microphoneFactory, options, logger);

                    logger.Information("Press {stopKey} to stop recording.", options.StopKey);
                    if (osType == "Unix")
                    {
                        result = await new NetCoreAudioMicrophoneTranscriber(logger, options.MicrophoneIndex)
                            .TranscribeAudioAsync(processor, () => CheckCancellation(options.StopKey), cts.Token);
                    }
                    else if (osType == "Win32NT")
                    {
                        result = await new MicrophoneTranscriber(logger, options.MicrophoneIndex)
                            .TranscribeAudioAsync(processor, () => CheckCancellation(options.StopKey), cts.Token);
                    }
                    else
                    {
                        logger.Error("Unsupported operating system: {osType}. Only Windows and Unix-like systems are supported.", osType);
                        return;
                    }
                }
                else
                {
                    FileInfo inputFile = new(options.InputFilePath);
                    if (!inputFile.Exists)
                    {
                        logger.Error("Input file does not exist: {inputFilePath}", options.InputFilePath);
                        return;
                    }

                    logger.Information(
                        "Robust long-file mode: primary={primary}, fallbacks={fallbacks}, language={language}, boundaryDetector={vad}, chunk={chunk}s, runtime={runtime}",
                        options.Model, options.FallbackModels, options.Language, options.UseVad, options.ChunkSeconds, options.Runtime);

                    var transcriber = new FileTranscriber(
                        logger,
                        (model, token) => GetWhisperModelPathAsync(model, logger, token));
                    result = await transcriber.TranscribeRobustAsync(inputFile, options, cts.Token);
                }

                if (options.OpenTextFile)
                {
                    OpenFile(result);
                }

                if (options.CopyToClipboard && result.Exists &&
                    string.Equals(result.Extension, ".txt", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(result.FullName, Encoding.UTF8, cts.Token);
                        TextCopy.ClipboardService.SetText(text);
                        logger.Information("Transcription result copied to clipboard.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Could not copy transcription result to clipboard; the transcription files were written successfully.");
                    }
                }

                await DelayBeforeExitAsync(options.DelaySeconds, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                logger.Information("Operation cancelled.");
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "WhisperCLI terminated because of an unhandled error.");
                Environment.ExitCode = -1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                instanceLock?.Dispose();
            }
        }

        private static void ValidateOptions(AppOptions options)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(options.DelaySeconds, nameof(options.DelaySeconds));
            ArgumentOutOfRangeException.ThrowIfNegative(options.MicrophoneIndex, nameof(options.MicrophoneIndex));
            ArgumentOutOfRangeException.ThrowIfNegative(options.GpuDevice, nameof(options.GpuDevice));

            string runtime = (options.Runtime ?? string.Empty).Trim().ToLowerInvariant();
            if (runtime is not ("auto" or "gpu" or "nvidia" or "cuda" or "cuda12" or "vulkan" or "cpu" or "cuda13"))
            {
                throw new ArgumentException("Runtime must be one of: auto, gpu, cuda, cuda12, vulkan, cpu.", nameof(options.Runtime));
            }
            if (runtime == "cuda13")
            {
                throw new ArgumentException("This compatibility build restores Whisper.net 1.8.1 (CUDA 12.x). Use auto/cuda, not cuda13.", nameof(options.Runtime));
            }

            if (options.ChunkSeconds < 20)
            {
                throw new ArgumentOutOfRangeException(nameof(options.ChunkSeconds), "Chunk duration must be at least 20 seconds.");
            }
            if (options.MaxChunkSeconds < options.ChunkSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaxChunkSeconds), "Max chunk duration must be greater than or equal to target chunk duration.");
            }
            if (options.MaxContextTokens < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaxContextTokens), "Max context tokens must be non-negative.");
            }
            if (options.EntropyThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.EntropyThreshold), "Entropy threshold must be positive.");
            }
            if (options.Temperature < 0 || options.Temperature > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options.Temperature), "Temperature must be in the range 0..1.");
            }
            if (options.TemperatureIncrement < 0 || options.TemperatureIncrement > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options.TemperatureIncrement), "Temperature increment must be in the range 0..1.");
            }
            if (options.GlitchThreshold <= 0 || options.GlitchThreshold > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options.GlitchThreshold), "Glitch threshold must be in the range (0, 1].");
            }
            if (options.MaxRecoveryDepth < 0 || options.MaxRecoveryDepth > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaxRecoveryDepth), "Recovery depth must be in the range 0..6.");
            }
            if (options.MinRecoverySplitSeconds < 5 || options.MinRecoverySplitSeconds >= options.ChunkSeconds / 2)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MinRecoverySplitSeconds),
                    "Minimum recovery split must be at least 5 seconds and less than half the target chunk duration.");
            }
            if (options.VadThreshold <= 0 || options.VadThreshold >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options.VadThreshold), "VAD threshold must be in the range (0, 1).");
            }
            if (options.VadMinSpeechMs < 0 || options.VadMinSilenceMs < 0 ||
                options.VadSpeechPaddingMs < 0 || options.VadEdgePaddingMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.UseVad), "VAD durations/padding must be non-negative.");
            }
            if (string.IsNullOrWhiteSpace(options.Language))
            {
                throw new ArgumentException("Language cannot be empty. Use 'auto' for automatic detection.", nameof(options.Language));
            }
        }

        private static WhisperProcessor CreateMicrophoneProcessor(
            WhisperFactory factory,
            AppOptions options,
            Serilog.ILogger logger)
        {
            logger.Information("Creating WhisperProcessor for microphone mode: {model}", options.Model);
            var builder = factory.CreateBuilder()
                .WithLanguage(options.Language)
                .WithMaxLastTextTokens(options.MaxContextTokens)
                .WithEntropyThreshold(options.EntropyThreshold)
                .WithTemperature(options.Temperature)
                .WithTemperatureInc(options.TemperatureIncrement);
            return builder.Build();
        }

        private static bool CheckCancellation(ConsoleKey stopKey)
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }

            ConsoleKey key = Console.ReadKey(true).Key;
            return key == stopKey;
        }

        private static void OpenFile(FileInfo fileInfo)
        {
            if (!fileInfo.Exists)
            {
                Log.Logger.Error("File does not exist: {filePath}", fileInfo.FullName);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fileInfo.FullName,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to open file: {filePath}", fileInfo.FullName);
            }
        }

        private static async Task<FileInfo> GetWhisperModelPathAsync(
            GgmlType model,
            Serilog.ILogger logger,
            CancellationToken token)
        {
            string modelName = $"ggml-{model.ToString().ToLowerInvariant()}.bin";
            string modelDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI", "Models");
            Directory.CreateDirectory(modelDirectory);

            string filePath = Path.Combine(modelDirectory, modelName);
            FileInfo fileInfo = new(filePath);

            // All supported Whisper models are far larger than this. A tiny file is almost
            // certainly a previously interrupted/non-atomic download.
            if (fileInfo.Exists && fileInfo.Length < 1024 * 1024)
            {
                logger.Warning("Existing model file looks incomplete and will be re-downloaded: {filePath}", filePath);
                fileInfo.Delete();
                fileInfo.Refresh();
            }

            if (fileInfo.Exists)
            {
                logger.Information("Model already exists: {filePath}", fileInfo.FullName);
                return fileInfo;
            }

            string tempPath = filePath + $".{Guid.NewGuid():N}.download";
            try
            {
                logger.Information("Downloading model: {model}", model);
                using Stream modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: token);
                await using (FileStream fileWriter = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await modelStream.CopyToAsync(fileWriter, token);
                    await fileWriter.FlushAsync(token);
                }
                File.Move(tempPath, filePath, overwrite: true);
                logger.Information("Model downloaded: {filePath}", filePath);
                return new FileInfo(filePath);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static async Task DelayBeforeExitAsync(int delaySeconds, CancellationToken token)
        {
            if (delaySeconds <= 0 || token.IsCancellationRequested)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
        }

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
                // Best-effort cleanup only.
            }
        }

        private sealed class SingleInstanceLock : IDisposable
        {
            private readonly FileStream _stream;
            private readonly string _path;

            private SingleInstanceLock(FileStream stream, string path)
            {
                _stream = stream;
                _path = path;
            }

            public static SingleInstanceLock? TryAcquire(Serilog.ILogger logger)
            {
                string workingDirectory = Path.Combine(Path.GetTempPath(), "WhisperCLI");
                Directory.CreateDirectory(workingDirectory);
                string path = Path.Combine(workingDirectory, "whisper.lock");

                try
                {
                    FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    stream.SetLength(0);
                    using (StreamWriter writer = new(stream, Encoding.UTF8, bufferSize: 256, leaveOpen: true))
                    {
                        writer.Write($"pid={Environment.ProcessId}; startedUtc={DateTime.UtcNow:O}");
                        writer.Flush();
                    }
                    stream.Position = 0;
                    logger.Debug("Single-instance lock acquired: {lockFilePath}", path);
                    return new SingleInstanceLock(stream, path);
                }
                catch (IOException)
                {
                    return null;
                }
            }

            public void Dispose()
            {
                _stream.Dispose();
                TryDelete(_path);
            }
        }
    }
}
