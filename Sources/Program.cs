using Serilog;
using Serilog.Core;
using System.Diagnostics;
using Whisper.net;
using Whisper.net.Ggml;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            CancellationTokenSource cts = new();
            Logger logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .CreateLogger();
            if (args.Length < 1)
            {
                logger.Error("Usage: WhisperCLI <inputFilePath> [modelType]");
                return;
            }
            string inputFilePath = args[0];
            FileInfo inputFile = new(inputFilePath);
            if (!inputFile.Exists)
            {
                logger.Error("Input file does not exist: {inputFilePath}", inputFilePath);
                return;
            }
            GgmlType model = args.Length > 1 && Enum.TryParse<GgmlType>(args[1], true, out var parsedType) ? parsedType : GgmlType.LargeV3Turbo;
            await TranscribeAudioAsync(inputFile, model, logger, cts.Token);
        }

        private static async Task<WhisperProcessor> CreateProcessorAsync(GgmlType model, ILogger logger, CancellationToken token)
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

        private static void Exec(string cmd)
        {
            var escapedArgs = cmd.Replace("\"", "\\\"");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\""
                }
            };

            process.Start();
            process.WaitForExit();
        }

        private static async Task CheckFfmpegAsync(ILogger logger, CancellationToken token)
        {
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "FFMpeg");
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }
            FFmpeg.SetExecutablesPath(workingDirectory);
            logger.Information("Checking FFmpeg...");
            if (Directory.GetFiles(workingDirectory).Length == 0)
            {
                logger.Information("FFmpeg not found - downloading...");
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpeg.ExecutablesPath, new FFMpegDownloadingProgress(Log.Logger));
                logger.Information("FFmpeg downloaded");
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    Exec("chmod +x " + Path.Combine(workingDirectory, "ffmpeg"));
                    Exec("chmod +x " + Path.Combine(workingDirectory, "ffprobe"));
                }
            }
        }

        private static async Task TranscribeAudioAsync(FileInfo inputFile, GgmlType model, ILogger logger, CancellationToken token)
        {
            using WhisperProcessor processor = await CreateProcessorAsync(model, logger, token);
            await CheckFfmpegAsync(logger, token);
        }
    }
}
