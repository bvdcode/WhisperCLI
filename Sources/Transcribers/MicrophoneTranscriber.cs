using Serilog;
using NAudio.Wave;
using Whisper.net;
using System.Text;
using Xabe.FFmpeg;
using System.Diagnostics;
using Xabe.FFmpeg.Downloader;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly int _microphoneIndex;

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

        private async Task<FileInfo> TranscodeWavToMp3Async(string wavPath, CancellationToken token)
        {
            await CheckFfmpegAsync(token);

            string mp3Path = Path.ChangeExtension(wavPath, ".mp3");
            var conversion = await FFmpeg.Conversions.FromSnippet.Convert(wavPath, mp3Path);
            conversion.OnProgress += (sender, args) =>
            {
                _logger.Information("Transcoding WAV to MP3: {argsPercent}%", args.Percent);
            };
            await conversion.Start(token);
            return new FileInfo(mp3Path);
        }

        public MicrophoneTranscriber(ILogger logger, int microphoneIndex)
        {
            _logger = logger;
            _microphoneIndex = microphoneIndex;
            _logger.Information("Available audio input devices: {deviceCount}", WaveInEvent.DeviceCount);
            if (WaveInEvent.DeviceCount == 0)
            {
                _logger.Error("No audio input devices found. Please connect a microphone.");
                throw new InvalidOperationException("No audio input devices found.");
            }
            if (microphoneIndex < 0 || microphoneIndex >= WaveInEvent.DeviceCount)
            {
                _logger.Error("Invalid microphone index: {index}. Valid range is 0 to {deviceCount}.", microphoneIndex, WaveInEvent.DeviceCount - 1);
                throw new ArgumentOutOfRangeException(nameof(microphoneIndex), "Invalid microphone index.");
            }
            _logger.Information("Using microphone[{index}]: {micName}", microphoneIndex, WaveInEvent.GetCapabilities(microphoneIndex).ProductName);
        }

        public async Task<FileInfo> TranscribeAudioAsync(
            Task<WhisperProcessor> processorTask,
            bool saveTranscript,
            Func<bool> stopRecording,
            CancellationToken token)
        {
            _logger.Information("Starting microphone recording...");

            var waveIn = new WaveInEvent
            {
                DeviceNumber = _microphoneIndex,
                WaveFormat = new WaveFormat(16000, 1)
            };

            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Recordings");
            var di = Directory.CreateDirectory(workingDirectory);
            string wavOutputPath = Path.Combine(di.FullName, "recording-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
            using var waveWriter = new WaveFileWriter(wavOutputPath, waveIn.WaveFormat);
            waveIn.DataAvailable += (s, a) =>
            {
                waveWriter.Write(a.Buffer, 0, a.BytesRecorded);
            };
            waveIn.StartRecording();
            while (!token.IsCancellationRequested)
            {
                bool stop = stopRecording?.Invoke() ?? false;
                if (stop)
                {
                    _logger.Information("Recording stopped by user request.");
                    break;
                }
                await Task.Delay(100, token); // Check for stop condition every 100ms
            }

            waveIn.StopRecording();
            waveWriter.Dispose();
            waveIn.Dispose();

            _logger.Information("Recording stopped. Transcribing...");
            using var audioStream = new MemoryStream(File.ReadAllBytes(wavOutputPath));
            StringBuilder sb = new();
            using var processor = await processorTask.ConfigureAwait(false);
            await foreach (var res in processor.ProcessAsync(audioStream, token))
            {
                _logger.Information("{lang}: {start}-{end} — {text}",
                    res.Language,
                    res.Start.ToString(@"hh\:mm\:ss"),
                    res.End.ToString(@"hh\:mm\:ss"),
                    res.Text);
                sb.Append(res.Text);
                if (token.IsCancellationRequested)
                {
                    break;
                }
            }
            _logger.Information("Transcription completed - wave file saved to {wavOutputPath}", wavOutputPath);
            if (sb.Length > 0)
            {
                string textFile = Path.ChangeExtension(wavOutputPath, ".txt");
                await File.WriteAllTextAsync(textFile, sb.ToString(), token);
                _logger.Information("Transcription saved to {textFile}", textFile);
                if (saveTranscript)
                {
                    string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string saveDirectory = Path.Combine(path, "WhisperCLI", "Transcripts");
                    Directory.CreateDirectory(saveDirectory);
                    string savedFilePath = Path.Combine(saveDirectory, Path.GetFileName(textFile));
                    File.Copy(textFile, savedFilePath, true);
                    
                    try
                    {
                        var mp3File = await TranscodeWavToMp3Async(wavOutputPath, token);
                        _logger.Information("MP3 file saved to {mp3OutputPath}", mp3File.FullName);
                        string savedMp3Path = Path.Combine(saveDirectory, Path.GetFileName(mp3File.FullName));
                        File.Copy(mp3File.FullName, savedMp3Path, true);
                        _logger.Information("MP3 copied to {savedMp3Path}", savedMp3Path);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to transcode recording to MP3");
                    }
                }
                return new(textFile);
            }
            else
            {
                _logger.Warning("No transcription results found. The audio may be too short or silent.");
                return new FileInfo(wavOutputPath); // Return the wav file even if no transcription was done
            }
        }
    }
}
