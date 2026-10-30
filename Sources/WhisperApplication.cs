using System.Diagnostics;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Whisper.net;
using Whisper.net.Logger;
using WhisperCLI.Transcribers;

namespace WhisperCLI
{
    internal static class WhisperApplication
    {
        public static async Task RunAsync(string[] args)
        {
            CommandLine.ParserResult<AppOptions> parserResult = CommandLine.Parser.Default.ParseArguments<AppOptions>(args);
            if (parserResult is not CommandLine.Parsed<AppOptions> parsedOptions)
            {
                return;
            }

            AppOptions options = parsedOptions.Value;
            ArgumentOutOfRangeException.ThrowIfNegative(options.DelaySeconds, "Delay seconds must be non-negative.");
            Console.OutputEncoding = Encoding.UTF8;
            CancellationTokenSource cts = new();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Logger logger = new LoggerConfiguration()
                .MinimumLevel.Is(options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            if (options.Update)
            {
                Environment.ExitCode = await Updates.UpdateCommand.RunAsync(options, logger, cts.Token);
                return;
            }

            if (options.BeamSize is < 1 or > 16 || !(options.VadThreshold > 0 && options.VadThreshold < 1))
            {
                logger.Error("--beam-size must be between 1 and 16; --vad-threshold must be between 0 and 1.");
                Environment.ExitCode = 1;
                return;
            }

            LogProvider.AddLogger((level, text) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    logger.Debug("[Whisper] [{level}] {text}", level.ToString().ToUpperInvariant(), text.Trim());
                }
            });

            await RunTranscriptionAsync(options, logger, cts.Token);
        }

        private static async Task RunTranscriptionAsync(AppOptions options, Logger logger, CancellationToken token)
        {
            if (options.UseLockfile && CheckLockfile(logger))
            {
                await DelayBeforeExitAsync(options, token);
                return;
            }

            TranscriptionInput? input = await MediaInputResolver.ResolveAsync(options, logger, token);
            if (input is null)
            {
                await DelayBeforeExitAsync(options, token);
                return;
            }

            if (input.Files.Count == 0 && !OperatingSystem.IsWindows())
            {
                logger.Error("Microphone recording requires Windows. Specify a media file or --folder.");
                Environment.ExitCode = 1;
                await DelayBeforeExitAsync(options, token);
                return;
            }

            FileInfo whisperModelInfo = await WhisperModelProvider.GetWhisperModelPathAsync(options.Model, logger, token);
            await using WhisperVadProcessor? vad = options.NoVad ? null : await WhisperModelProvider.CreateVadAsync(options.VadThreshold, logger, token);
            Task<WhisperProcessor> processorTask = WhisperModelProvider.CreateProcessorAsync(options.Model, whisperModelInfo, logger, options.Language, options.BeamSize, token);
            try
            {
                List<FileInfo> results = await TranscribeAsync(input, options, processorTask, vad, logger, token);
                await HandleResultsAsync(results, options, logger, token);
                await DelayBeforeExitAsync(options, token);
            }
            catch (OperationCanceledException)
            {
                logger.Information("Operation canceled by user.");
            }
            finally
            {
                string lockFilePath = GetLockFileLocation();
                File.Delete(lockFilePath);
            }
        }

        private static async Task<List<FileInfo>> TranscribeAsync(
            TranscriptionInput input,
            AppOptions options,
            Task<WhisperProcessor> processorTask,
            WhisperVadProcessor? vad,
            Logger logger,
            CancellationToken token)
        {
            if (input.Files.Count > 0)
            {
                return await TranscribeFilesAsync(input.Files, processorTask, vad, options.Language == "auto", options.Format, input.FolderMode, logger, token);
            }

            logger.Information("Press {stopKey} to stop recording.", options.StopKey);
            FileInfo result = await new MicrophoneTranscriber(logger, options.MicrophoneIndex, vad, options.Language == "auto")
                .TranscribeAudioAsync(processorTask, options.SaveTranscript, OutputFormat.Txt, () => CheckCancellation(options.StopKey), token);
            return [result];
        }

        private static async Task HandleResultsAsync(IReadOnlyList<FileInfo> results, AppOptions options, Logger logger, CancellationToken token)
        {
            if (results.Count == 1 && options.OpenTextFile)
            {
                OpenFile(results[0]);
            }
            else if (results.Count > 1 && options.OpenTextFile)
            {
                logger.Information("Skipping --open-results because multiple output files were generated.");
            }

            if (results.Count == 1 && options.CopyToClipboard)
            {
                try
                {
                    string text = await File.ReadAllTextAsync(results[0].FullName, Encoding.UTF8, token);
                    await TextCopy.ClipboardService.SetTextAsync(text, token);
                    logger.Information("Transcription result copied to clipboard.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to copy transcription result to clipboard.");
                }
            }
            else if (results.Count > 1 && options.CopyToClipboard)
            {
                logger.Information("Skipping clipboard copy because multiple output files were generated.");
            }
        }

        private static async Task DelayBeforeExitAsync(AppOptions options, CancellationToken token)
        {
            if (options.DelaySeconds > 0)
            {
                await Task.Delay(options.DelaySeconds * 1000, token);
            }
        }

        private static async Task<List<FileInfo>> TranscribeFilesAsync(
            IReadOnlyList<FileInfo> inputFiles,
            Task<WhisperProcessor> processorTask,
            WhisperVadProcessor? vad,
            bool detectLanguage,
            OutputFormat format,
            bool skipExisting,
            Logger logger,
            CancellationToken token)
        {
            List<FileInfo> results = [];
            await using WhisperProcessor processor = await processorTask.ConfigureAwait(false);
            FileTranscriber transcriber = new(logger, vad, detectLanguage);
            for (int i = 0; i < inputFiles.Count; i++)
            {
                FileInfo inputFile = inputFiles[i];
                string outputPath = TranscriptFormatter.GetOutputPath(inputFile, format);
                if (skipExisting && File.Exists(outputPath))
                {
                    logger.Information("Skipping {inputFile}: transcript already exists at {outputPath}.", inputFile.FullName, outputPath);
                    continue;
                }

                logger.Information("Processing file {current}/{total}: {inputFile}", i + 1, inputFiles.Count, inputFile.FullName);
                FileInfo result = await transcriber.TranscribeAudioAsync(inputFile, processor, format, token).ConfigureAwait(false);
                results.Add(result);
            }

            return results;
        }

        private static string GetLockFileLocation()
        {
            return AppPaths.LockFilePath;
        }

        private static bool CheckLockfile(Logger logger)
        {
            string lockFilePath = GetLockFileLocation();
            if (File.Exists(lockFilePath))
            {
                logger.Warning("Lock file exists. WhisperCLI may already be running.");
                return true;
            }
            try
            {
                using (File.Create(lockFilePath)) { }
                logger.Debug("Lock file created: {lockFilePath}", lockFilePath);
                return false;
            }
            catch (IOException ex)
            {
                logger.Error(ex, "Failed to create lock file: {lockFilePath}", lockFilePath);
                return true;
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => File.Delete(lockFilePath);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => File.Delete(lockFilePath);
                Console.CancelKeyPress += (s, e) => File.Delete(lockFilePath);
            }
        }

        private static bool CheckCancellation(ConsoleKey stopKey)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKey key = Console.ReadKey(true).Key;
                return key == stopKey;
            }
            return false;
        }

        private static void OpenFile(FileInfo fileInfo)
        {
            if (fileInfo.Exists)
            {
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
            else
            {
                Log.Logger.Error("File does not exist: {filePath}", fileInfo.FullName);
            }
        }
    }
}
