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
            if (string.IsNullOrWhiteSpace(options.InputFilePath))
            {
                FileInfo whisperModelInfo = await GetWhisperModelPathAsync(options.Model, logger, cts.Token);
                FileInfo sileroVadModelInfo = await GetSileroPathAsync(logger, cts.Token);
                await new MicrophoneTranscriber(logger, whisperModelInfo.FullName, sileroVadModelInfo.FullName, options.MicrophoneIndex).TranscribeAudioAsync(cts.Token);
            }
            else
            {
                FileInfo whisperModelInfo = await GetWhisperModelPathAsync(options.Model, logger, cts.Token);
                using var processor = CreateProcessor(options.Model, whisperModelInfo, logger);
                FileInfo inputFile = new(options.InputFilePath);
                if (!inputFile.Exists)
                {
                    logger.Error("Input file does not exist: {inputFilePath}", options.InputFilePath);
                    return;
                }
                await new FileTranscriber(logger).TranscribeAudioAsync(inputFile, processor, cts.Token);
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

        private static async Task<FileInfo> GetSileroPathAsync(Logger logger, CancellationToken token)
        {
            const string url = "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Models", "SileroVad");
            var di = Directory.CreateDirectory(workingDirectory);

            string filePath = Path.Combine(di.FullName, "silero_vad.onnx");
            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists)
            {
                logger.Information("Downloading Silero VAD model...");
                using var client = new HttpClient();
                using var response = await client.GetAsync(url, token);
                response.EnsureSuccessStatusCode();
                using var fileWriter = fileInfo.Create();
                await response.Content.CopyToAsync(fileWriter, token);
                logger.Information("Silero VAD model downloaded: {filePath}", fileInfo.FullName);
            }
            else
            {
                logger.Information("Silero VAD model already exists: {filePath}", fileInfo.FullName);
            }
            return fileInfo;
        }

        private static WhisperProcessor CreateProcessor(GgmlType model, FileInfo whisperModelInfo, Logger logger)
        {
            logger.Information("Creating WhisperProcessor...");
            try
            {
                WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelInfo.FullName);
                logger.Information("WhisperProcessor created: {model}", model);
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
