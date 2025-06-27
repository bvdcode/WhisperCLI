using Serilog;
using System.Text;
using Whisper.net;
using Xabe.FFmpeg;
using System.Diagnostics;
using Xabe.FFmpeg.Downloader;

namespace WhisperCLI.Transcribers
{
    public class FileTranscriber(ILogger _logger)
    {
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

        private async Task CheckFfmpegAsync(CancellationToken token)
        {
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "FFMpeg");
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }
            FFmpeg.SetExecutablesPath(workingDirectory);
            _logger.Information("Checking FFmpeg...");
            if (Directory.GetFiles(workingDirectory).Length == 0)
            {
                _logger.Information("FFmpeg not found - downloading...");
                var task1 = FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpeg.ExecutablesPath, new FFMpegDownloadingProgress(_logger));
                var task2 = Task.Delay(600_000, token);
                await Task.WhenAny(task1, task2);
                _logger.Information("FFmpeg downloaded");
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    Exec("chmod +x " + Path.Combine(workingDirectory, "ffmpeg"));
                    Exec("chmod +x " + Path.Combine(workingDirectory, "ffprobe"));
                }
            }
        }

        private async Task<MemoryStream> ConvertToWaveStreamAsync(FileInfo inputFile)
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
                _logger.Information("Converting media to wave: {argsPercent}%", args.Percent);
            };
            await conversion.Start();
            byte[] bytes = File.ReadAllBytes(targetFile);
            MemoryStream ms = new(bytes);
            File.Delete(targetFile);
            return ms;
        }

        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, Task<WhisperProcessor> processorTask, CancellationToken token)
        {
            await CheckFfmpegAsync(token);
            MemoryStream waves = await ConvertToWaveStreamAsync(inputFile);
            StringBuilder sb = new();
            Stopwatch sw = Stopwatch.StartNew();
            string prev = string.Empty;
            _logger.Information("Starting transcription for {inputFile}", inputFile.Name);
            using var processor = await processorTask.ConfigureAwait(false);
            await foreach (var result in processor.ProcessAsync(waves, token))
            {
                if (result.Text == prev)
                {
                    continue;
                }
                sb.Append(result.Text);
                prev = result.Text;
                _logger.Information("{lang}: {start}->{end}: {text}", result.Language,
                    result.Start.ToString(@"hh\:mm\:ss"), result.End.ToString(@"hh\:mm\:ss"), result.Text);
                if (token.IsCancellationRequested)
                {
                    _logger.Information("Cancellation requested - stopping recognition");
                    break;
                }
            }
            _logger.Information("Elapsed: {el}", sw.Elapsed.ToString(@"hh\:mm\:ss"));
            string textFilePath = Path.ChangeExtension(inputFile.FullName, ".txt");
            File.WriteAllText(textFilePath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Transcription complete. Output saved to: {textFilePath}", textFilePath);
            return new FileInfo(textFilePath);
        }
    }
}