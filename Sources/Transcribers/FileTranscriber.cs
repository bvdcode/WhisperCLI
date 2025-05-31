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
            _logger.Information("Preparing WAV (16kHz, mono) from: {inputFile}", inputFile.FullName);

            string tempOutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");

            bool isWav = inputFile.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);

            var conversion = FFmpeg.Conversions.New();

            if (isWav)
            {
                // Always resample WAV to match required spec
                conversion.AddParameter($"-i \"{inputFile.FullName}\"", ParameterPosition.PreInput);
            }
            else
            {
                bool isVideo = inputFile.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                            inputFile.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                            inputFile.Extension.Equals(".avi", StringComparison.OrdinalIgnoreCase);

                conversion = isVideo
                    ? await FFmpeg.Conversions.FromSnippet.ExtractAudio(inputFile.FullName, tempOutputPath)
                    : await FFmpeg.Conversions.FromSnippet.Convert(inputFile.FullName, tempOutputPath);
            }

            // Apply format settings
            conversion.SetOutput(tempOutputPath);
            conversion.AddParameter("-ar 16000", ParameterPosition.PostInput);       // 16 kHz
            conversion.AddParameter("-ac 1", ParameterPosition.PostInput);           // mono
            conversion.AddParameter("-sample_fmt s16", ParameterPosition.PostInput); // 16-bit signed

            conversion.OnProgress += (s, args) =>
            {
                _logger.Information("Converting to WAV: {Percent}%", args.Percent);
            };

            await conversion.Start();

            byte[] bytes = await File.ReadAllBytesAsync(tempOutputPath);
            MemoryStream ms = new(bytes);
            File.Delete(tempOutputPath);

            return ms;
        }


        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, WhisperProcessor processor, CancellationToken token)
        {
            await CheckFfmpegAsync(token);
            MemoryStream waves = await ConvertToWaveStreamAsync(inputFile);
            StringBuilder sb = new();
            Stopwatch sw = Stopwatch.StartNew();
            string prev = string.Empty;
            _logger.Information("Starting transcription for {inputFile}", inputFile.Name);
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
            await processor.DisposeAsync();
            return new FileInfo(textFilePath);
        }
    }
}