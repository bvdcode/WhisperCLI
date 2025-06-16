using Serilog;
using System.Text;
using Whisper.net;
using Serilog.Core;
using Serilog.Events;
using Whisper.net.Ggml;
using System.Diagnostics;
using Whisper.net.Logger;
using WhisperCLI.Transcribers;

namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            AppOptions options = CommandLine.Parser.Default.ParseArguments<AppOptions>(args).Value;
            ArgumentOutOfRangeException.ThrowIfNegative(options.DelaySeconds, "Delay seconds must be non-negative.");
            Console.OutputEncoding = Encoding.UTF8;
            CancellationTokenSource cts = new();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true; // Prevent the process from terminating immediately
                cts.Cancel(); // Signal cancellation
            };
            Logger logger = new LoggerConfiguration()
                .MinimumLevel.Is(options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            LogProvider.AddLogger((level, text) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    logger.Debug("[Whisper] [{level}] {text}", level.ToString().ToUpperInvariant(), text.Trim());
                }
            });
            if (CheckLockfile(logger))
            {
                await Task.Delay(options.DelaySeconds * 1000, cts.Token);
                return;
            }
            FileInfo whisperModelInfo = await GetWhisperModelPathAsync(options.Model, logger, cts.Token);
            using var processor = CreateProcessor(options.Model, whisperModelInfo, logger, options.Language);
            FileInfo result;
            if (string.IsNullOrWhiteSpace(options.InputFilePath))
            {
                logger.Information("Press {stopKey} to stop recording.", options.StopKey);
                result = await new MicrophoneTranscriber(logger, options.MicrophoneIndex)
                    .TranscribeAudioAsync(processor, () => CheckCancellation(options.StopKey), cts.Token);
            }
            else
            {
                FileInfo inputFile = new(options.InputFilePath);
                if (!inputFile.Exists)
                {
                    logger.Error("Input file does not exist: {inputFilePath}", options.InputFilePath);
                    return;
                }
                result = await new FileTranscriber(logger)
                    .TranscribeAudioAsync(inputFile, processor, cts.Token);
            }
            if (options.OpenTextFile)
            {
                OpenFile(result);
            }
            if (options.CopyToClipboard)
            {
                try
                {
                    string text = File.ReadAllText(result.FullName, Encoding.UTF8);
                    TextCopy.ClipboardService.SetText(text);
                    logger.Information("Transcription result copied to clipboard.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to copy transcription result to clipboard.");
                }
            }
            await Task.Delay(options.DelaySeconds * 1000, cts.Token);
        }

        private static bool CheckLockfile(Logger logger)
        {
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI");
            var di = Directory.CreateDirectory(workingDirectory);
            string lockFilePath = Path.Combine(di.FullName, "whisper.lock");
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
                // Ensure the lock file is deleted on exit
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

        private static async Task<FileInfo> GetWhisperModelPathAsync(GgmlType model, Logger logger, CancellationToken token)
        {
            string modelName = $"ggml-{model.ToString().ToLower()}.bin";
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Models");
            var di = Directory.CreateDirectory(workingDirectory);

            string filePath = Path.Combine(di.FullName, modelName);
            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists)
            {
                using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: token);
                logger.Information("Downloading model: {_ggmlType}", model);
                using var fileWriter = fileInfo.Create();
                await modelStream.CopyToAsync(fileWriter, token);
                logger.Information("Model downloaded: {filePath}", fileInfo.FullName);
            }
            else
            {
                logger.Information("Model already exists: {filePath}", fileInfo.FullName);
            }
            return fileInfo;
        }

        private static WhisperProcessor CreateProcessor(GgmlType model, FileInfo whisperModelInfo, Logger logger, string language)
        {
            logger.Information("Creating WhisperProcessor with language: {language}, model: {model}...", language, model);
            try
            {
                WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelInfo.FullName);
                logger.Information("WhisperProcessor created: {model}", model);
                return whisperFactory
                    .CreateBuilder()
                    .WithLanguage(language)
                    .Build();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error occurred while creating WhisperProcessor");
                Environment.Exit(-1);
                throw;
            }
        }
    }
}
