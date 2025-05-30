using EchoSharp.Abstractions.SpeechTranscription;
using EchoSharp.NAudio;
using EchoSharp.Onnx.SileroVad;
using EchoSharp.SpeechTranscription;
using EchoSharp.Whisper.net;
using NAudio.Wave;
using Serilog;
using System.Globalization;
using System.Reflection;
using Whisper.net;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly MicrophoneInputSource _micSource;
        private readonly IRealtimeSpeechTranscriptor _transcriptor;

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
                Threshold = 0.2f
            });
            WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelPath);
            var whisperProcessorBuilder = whisperFactory.CreateBuilder().WithLanguage("auto");
            var speechFactory = new WhisperSpeechTranscriptorFactory(builder: whisperProcessorBuilder);
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
