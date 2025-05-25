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

namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            const GgmlType defaultModel = GgmlType.LargeV3Turbo;
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
            if (args.Length < 1)
            {
                logger.Error("Usage: WhisperCLI <inputFilePath> [modelType]");
                logger.Error("Available models: {models}", string.Join(", ", Enum.GetNames<GgmlType>()));
                logger.Error("Default model: {defaultModel}", defaultModel);
                logger.Error("Example: WhisperCLI audio.mp3");
                logger.Error("Example: WhisperCLI audio.mp3 {defaultModel}", defaultModel);
                return;
            }
            string inputFilePath = args[0];
            FileInfo inputFile = new(inputFilePath);
            if (!inputFile.Exists)
            {
                logger.Error("Input file does not exist: {inputFilePath}", inputFilePath);
                return;
            }
            GgmlType model = args.Length > 1 && Enum.TryParse<GgmlType>(args[1], true, out var parsedType) ? parsedType : defaultModel;
            await TranscribeAudioAsync(inputFile, model, logger, cts.Token);
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

        private static async Task CheckFfmpegAsync(Logger logger, CancellationToken token)
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
                var task1 = FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpeg.ExecutablesPath, new FFMpegDownloadingProgress(Log.Logger));
                var task2 = Task.Delay(600_000, token);
                await Task.WhenAny(task1, task2);
                logger.Information("FFmpeg downloaded");
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    Exec("chmod +x " + Path.Combine(workingDirectory, "ffmpeg"));
                    Exec("chmod +x " + Path.Combine(workingDirectory, "ffprobe"));
                }
            }
        }

        private static async Task<MemoryStream> ConvertToWaveStreamAsync(FileInfo inputFile, Logger logger)
        {
            string targetFile = Path.ChangeExtension(inputFile.FullName, ".wav");
            bool isVideo = inputFile.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                           inputFile.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                           inputFile.Extension.Equals(".avi", StringComparison.OrdinalIgnoreCase);
            var conversion = isVideo
                ? await FFmpeg.Conversions.FromSnippet.ExtractAudio(inputFile.FullName, targetFile)
                : await FFmpeg.Conversions.FromSnippet.Convert(inputFile.FullName, targetFile);

            conversion.AddParameter("-ar 16000", ParameterPosition.PostInput);
            conversion.OnProgress += (sender, args) =>
            {
                logger.Information("Converting media to wave: {argsPercent}%", args.Percent);
            };
            await conversion.Start();
            byte[] bytes = File.ReadAllBytes(targetFile);
            MemoryStream ms = new(bytes);
            File.Delete(targetFile);
            return ms;
        }

        private static async Task TranscribeAudioAsync(FileInfo inputFile, GgmlType model, Logger logger, CancellationToken token)
        {
            WhisperProcessor processor = await CreateProcessorAsync(model, logger, token);
            await CheckFfmpegAsync(logger, token);
            MemoryStream waves = await ConvertToWaveStreamAsync(inputFile, logger);
            StringBuilder sb = new();
            Stopwatch sw = Stopwatch.StartNew();
            string prev = string.Empty;
            logger.Information("Starting transcription for {inputFile} using model {model}", inputFile.Name, model);
            await foreach (var result in processor.ProcessAsync(waves, token))
            {
                if (result.Text == prev)
                {
                    continue;
                }
                sb.Append(result.Text);
                prev = result.Text;
                logger.Information("{lang}: {start}->{end}: {text}", result.Language,
                    result.Start.ToString("HH:mm:ss"), result.End.ToString("HH:mm:ss"), result.Text);
                if (token.IsCancellationRequested)
                {
                    logger.Information("Cancellation requested - stopping recognition");
                    break;
                }
            }
            logger.Information("Elapsed: {el} | GGML: {model}", sw.Elapsed.ToString(@"hh\:mm\:ss"), model);
            string textFilePath = Path.ChangeExtension(inputFile.FullName, ".txt");
            File.WriteAllText(textFilePath, sb.ToString(), Encoding.UTF8);
            logger.Information("Transcription complete. Output saved to: {textFilePath}", textFilePath);
            await processor.DisposeAsync();
        }
    }
}
