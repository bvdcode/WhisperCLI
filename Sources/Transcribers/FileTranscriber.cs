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
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Conversions");
            Directory.CreateDirectory(workingDirectory);
            string targetFile = Path.Combine(workingDirectory, $"{Path.GetFileNameWithoutExtension(inputFile.Name)}-{Guid.NewGuid():N}.wav");
            bool isVideo = IsVideoFile(inputFile);
            var conversion = isVideo
                ? await FFmpeg.Conversions.FromSnippet.ExtractAudio(inputFile.FullName, targetFile)
                : await FFmpeg.Conversions.FromSnippet.Convert(inputFile.FullName, targetFile);

            conversion.AddParameter("-ar 16000", ParameterPosition.PostInput);
            conversion.OnProgress += (sender, args) =>
            {
                _logger.Information("Converting media to wave: {argsPercent}%", args.Percent);
            };
            try
            {
                await conversion.Start();
                byte[] bytes = await File.ReadAllBytesAsync(targetFile);
                return new MemoryStream(bytes);
            }
            finally
            {
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
            }
        }

        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, Task<WhisperProcessor> processorTask, OutputFormat format, CancellationToken token)
        {
            using var processor = await processorTask.ConfigureAwait(false);
            return await TranscribeAudioAsync(inputFile, processor, format, token);
        }

        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, WhisperProcessor processor, OutputFormat format, CancellationToken token)
        {
            await CheckFfmpegAsync(token);
            await using MemoryStream waves = await ConvertToWaveStreamAsync(inputFile);
            List<TranscriptSegment> segments = [];
            Stopwatch sw = Stopwatch.StartNew();
            string prev = string.Empty;
            _logger.Information("Starting transcription for {inputFile}", inputFile.Name);
            await foreach (var result in processor.ProcessAsync(waves, token))
            {
                if (result.Text == prev)
                {
                    continue;
                }
                segments.Add(new TranscriptSegment(result.Start, result.End, result.Text));
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
            string outputFilePath = Path.ChangeExtension(inputFile.FullName, TranscriptFormatter.GetFileExtension(format));
            File.WriteAllText(outputFilePath, TranscriptFormatter.Format(segments, format), Encoding.UTF8);
            _logger.Information("Transcription complete. Output saved to: {outputFilePath}", outputFilePath);
            return new FileInfo(outputFilePath);
        }

        private static bool IsVideoFile(FileInfo inputFile)
        {
            return inputFile.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
        }
    }
}
