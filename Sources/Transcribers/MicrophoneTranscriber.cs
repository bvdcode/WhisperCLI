using Serilog;
using NAudio.Wave;
using Whisper.net;
using System.Text;
using WhisperCLI.Transcribers;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber : IMicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly int _microphoneIndex;

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

        public async Task<FileInfo> TranscribeAudioAsync(WhisperProcessor processor, Func<bool> stopRecording, CancellationToken token)
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
