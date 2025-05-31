using Whisper.net;

namespace WhisperCLI.Transcribers
{
    public interface IMicrophoneTranscriber
    {
        public abstract Task<FileInfo> TranscribeAudioAsync(WhisperProcessor processor, Func<bool> stopRecording, CancellationToken token);

    };
}
