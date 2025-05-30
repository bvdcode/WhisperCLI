using Serilog;
using Whisper.net;
using NAudio.Wave;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly WaveInEvent _waveIn;

        public MicrophoneTranscriber(ILogger logger, int microphoneIndex)
        {
            _logger = logger;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = microphoneIndex,                      // первый микрофон
                WaveFormat = new WaveFormat(16000, 1)  // 16 kHz, моно
            };
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
            var deviceName = WaveInEvent.GetCapabilities(microphoneIndex).ProductName;
            _logger.Information("Device {index}: {deviceName}", microphoneIndex, deviceName);
        }

        public async Task TranscribeAudioAsync(WhisperProcessor processor, CancellationToken token)
        {
            _waveIn.DataAvailable += async (s, e) =>
            {
                using var ms = new MemoryStream();
                using (var writer = new WaveFileWriter(ms, _waveIn.WaveFormat))
                {
                    writer.Write(e.Buffer, 0, e.BytesRecorded);
                    writer.Flush();
                }
                ms.Position = 0;

                await foreach (var res in processor.ProcessAsync(ms, token))
                {
                    _logger.Information("{lang}: {start}-{end} — {text}",
                        res.Language,
                        res.Start.ToString(@"hh\:mm\:ss"),
                        res.End.ToString(@"hh\:mm\:ss"),
                        res.Text);
                    if (token.IsCancellationRequested) break;
                }
            };

            _waveIn.RecordingStopped += (s, e) =>
            {
                _logger.Information("Recording stopped");
                processor.DisposeAsync().AsTask().Wait();
            };

            _logger.Information("Starting recording from microphone {microphoneIndex}...", _waveIn.DeviceNumber);
            _waveIn.StartRecording();

            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Recording cancelled by user");
                _waveIn.StopRecording();
            }
        }
    }
}
