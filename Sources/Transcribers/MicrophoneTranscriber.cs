using Serilog;
using NAudio.Wave;
using EchoSharp.NAudio;
using System.Globalization;
using EchoSharp.Whisper.net;
using EchoSharp.Onnx.SileroVad;
using EchoSharp.SpeechTranscription;
using EchoSharp.Abstractions.SpeechTranscription;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly IRealtimeSpeechTranscriptor _transcriptor;
        private readonly MicrophoneInputSource _micSource;

        public MicrophoneTranscriber(ILogger logger, string whisperModelPath, string sileroVadModelPath, int microphoneIndex)
        {
            _logger = logger;
            int deviceCount = WaveInEvent.DeviceCount;
            if (deviceCount == 0)
            {
                _logger.Error("No audio input devices found.");
                throw new InvalidOperationException("No microphone detected.");
            }

            _logger.Information("Found {count} audio input device(s):", deviceCount);
            for (int i = 0; i < deviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                _logger.Information("  Device {index}: {name}", i, caps.ProductName);
            }

            if (microphoneIndex < 0 || microphoneIndex >= deviceCount)
            {
                _logger.Error("Invalid microphoneIndex {idx}. Valid range is 0–{max}.", microphoneIndex, deviceCount - 1);
                throw new ArgumentOutOfRangeException(nameof(microphoneIndex));
            }
            _logger.Information("Using microphone {index}: {name}", microphoneIndex, WaveInEvent.GetCapabilities(microphoneIndex).ProductName);
            var vadFactory = new SileroVadDetectorFactory(new SileroVadOptions(sileroVadModelPath)
            {
                Threshold = 0.5f
            });

            var speechFactory = new WhisperSpeechTranscriptorFactory(whisperModelPath);
            var realtimeFactory = new EchoSharpRealtimeTranscriptorFactory(speechFactory, vadFactory);
            _transcriptor = realtimeFactory.Create(new RealtimeSpeechTranscriptorOptions
            {
                AutodetectLanguageOnce = false,
                IncludeSpeechRecogizingEvents = false,
                LanguageAutoDetect = true,
                Language = CultureInfo.InvariantCulture
            });
            _micSource = new MicrophoneInputSource(deviceNumber: microphoneIndex);
        }

        public async Task TranscribeAudioAsync(CancellationToken token)
        {
            _logger.Information("Starting microphone recording...");
            _micSource.StartRecording();

            await foreach (var evt in _transcriptor.TranscribeAsync(_micSource, token))
            {
                if (evt is RealtimeSegmentRecognized rec)
                {
                    var start = rec.Segment.StartTime.ToString(@"hh\:mm\:ss");
                    var end = (rec.Segment.StartTime + rec.Segment.Duration).ToString(@"hh\:mm\:ss");
                    _logger.Information("{start}-{end} — {text}", start, end, rec.Segment.Text);
                }
            }

            _logger.Information("Stopping microphone recording...");
            _micSource.StopRecording();
        }
    }
}
