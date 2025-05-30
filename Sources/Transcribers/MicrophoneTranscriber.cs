using Serilog;
using Whisper.net;

namespace WhisperCLI.Transcribers
{
    public class MicrophoneTranscriber(ILogger _logger)
    {
        public async Task TranscribeAudioAsync(WhisperProcessor processor, CancellationToken token)
        {

        }
    }
}