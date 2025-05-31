using Serilog;
using NAudio.Wave;
using Whisper.net;
using System.Text;

namespace WhisperCLI.Transcribers
{
    public interface IMicrophoneTranscriber
    {
        public abstract Task<FileInfo> TranscribeAudioAsync(WhisperProcessor processor, Func<bool> stopRecording, CancellationToken token);

    };
}
