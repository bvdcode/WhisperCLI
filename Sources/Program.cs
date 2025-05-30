using Serilog;
using Xabe.FFmpeg;
using System.Text;
using Whisper.net;
using Serilog.Core;
using Serilog.Events;
using Whisper.net.Ggml;
using Whisper.net.Logger;
using System.Diagnostics;
using Xabe.FFmpeg.Downloader;
using WhisperCLI.Transcribers;

namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            AppOptions options = CommandLine.Parser.Default.ParseArguments<AppOptions>(args).Value;
            Console.OutputEncoding = Encoding.UTF8;
            CancellationTokenSource cts = new();
            Logger logger = new LoggerConfiguration()
                .MinimumLevel.Is(Debugger.IsAttached ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            LogProvider.AddLogger((level, text) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    logger.Debug("[Whisper] [{level}] {text}", level.ToString().ToUpperInvariant(), text.Trim());
                }
            });
            string inputFilePath = args[0];
            FileInfo inputFile = new(inputFilePath);
            if (!inputFile.Exists)
            {
                logger.Error("Input file does not exist: {inputFilePath}", inputFilePath);
                return;
            }
            using var processor = await CreateProcessorAsync(options.Model, logger, cts.Token);
            await new FileTranscriber(logger).TranscribeAudioAsync(inputFile, processor, cts.Token);
        }

        private static async Task<WhisperProcessor> CreateProcessorAsync(GgmlType model, Logger logger, CancellationToken token)
        {
            logger.Information("Creating WhisperProcessor...");
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

            try
            {
                WhisperFactory whisperFactory = WhisperFactory.FromPath(fileInfo.FullName);
                logger.Information("WhisperProcessor created: {model}", modelName);
                return whisperFactory
                    .CreateBuilder()
                    .WithLanguage("auto")
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
