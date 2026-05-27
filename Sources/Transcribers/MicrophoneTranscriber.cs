using Serilog;
using NAudio.Wave;
using Whisper.net;
using System.Text;
using Xabe.FFmpeg;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly int _microphoneIndex;

        private async Task CheckFfmpegAsync(CancellationToken token)
        {
            await FFmpegBootstrapper.EnsureAvailableAsync(_logger, token).ConfigureAwait(false);
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
            OutputFormat format,
            Func<bool> stopRecording,
            CancellationToken token)
        {
            _logger.Information("Starting microphone recording...");

            var waveIn = new WaveInEvent
            {
                DeviceNumber = _microphoneIndex,
                WaveFormat = new WaveFormat(16000, 1)
            };

            string wavOutputPath = Path.Combine(AppPaths.RecordingsDirectory, "recording-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
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
            List<TranscriptSegment> segments = [];
            using var processor = await processorTask.ConfigureAwait(false);
            string prev = string.Empty;
            await foreach (var res in processor.ProcessAsync(audioStream, token))
            {
                if (res.Text == prev)
                {
                    continue;
                }
                prev = res.Text;
                _logger.Information("{lang}: {start}-{end} — {text}",
                    res.Language,
                    res.Start.ToString(@"hh\:mm\:ss"),
                    res.End.ToString(@"hh\:mm\:ss"),
                    res.Text);
                segments.Add(new TranscriptSegment(res.Start, res.End, res.Text));
                if (token.IsCancellationRequested)
                {
                    break;
                }
            }
            _logger.Information("Transcription completed - wave file saved to {wavOutputPath}", wavOutputPath);
            if (segments.Count > 0)
            {
                string outputFile = Path.ChangeExtension(wavOutputPath, TranscriptFormatter.GetFileExtension(format));
                await File.WriteAllTextAsync(outputFile, TranscriptFormatter.Format(segments, format), token);
                _logger.Information("Transcription saved to {outputFile}", outputFile);
                if (saveTranscript)
                {
                    string saveDirectory = AppPaths.TranscriptsDirectory;
                    string savedFilePath = Path.Combine(saveDirectory, Path.GetFileName(outputFile));
                    File.Copy(outputFile, savedFilePath, true);
                    
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
                return new(outputFile);
            }
            else
            {
                _logger.Warning("No transcription results found. The audio may be too short or silent.");
                return new FileInfo(wavOutputPath); // Return the wav file even if no transcription was done
            }
        }
    }
}
