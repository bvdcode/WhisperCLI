using Serilog;
using NAudio.Wave;
using Whisper.net;
using System.Text;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber
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

        public async Task TranscribeAudioAsync(WhisperProcessor processor, Action<FileInfo> callback, CancellationToken token)
        {
            _logger.Information("Starting microphone recording...");

            var waveIn = new WaveInEvent
            {
                DeviceNumber = _microphoneIndex,
                WaveFormat = new WaveFormat(16000, 1)
            };

            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Models", "SileroVad");
            var di = Directory.CreateDirectory(workingDirectory);
            string wavOutputPath = Path.Combine(di.FullName, "recording-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
            using var waveWriter = new WaveFileWriter(wavOutputPath, waveIn.WaveFormat);
            waveIn.DataAvailable += (s, a) =>
            {
                waveWriter.Write(a.Buffer, 0, a.BytesRecorded);
            };
            waveIn.StartRecording();
            ConsoleKey stopKey = ConsoleKey.Spacebar;
            _logger.Information("Press {stopKey} to stop recording.", stopKey);
            CancellationTokenSource cts = new();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true; // Prevent the process from terminating
                cts.Cancel(); // Cancel the recording
                _logger.Information("Recording cancellation requested.");
            };
            try
            {
                while (!token.IsCancellationRequested && !cts.IsCancellationRequested)
                {
                    await Task.Delay(100, token);
                }
            }
            catch (TaskCanceledException) { }

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
                string textFile = Path.Combine(di.FullName, "transcription-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                await File.WriteAllTextAsync(textFile, sb.ToString(), token);
                _logger.Information("Transcription saved to {textFile}", textFile);
                callback(new FileInfo(textFile));
            }
        }
    }
}
