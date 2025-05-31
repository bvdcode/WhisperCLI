using Serilog;
using Whisper.net;
using System.Text;
using WhisperCLI.AudioHandlers;

namespace WhisperCLI.Transcribers
{
    public class NetCoreAudioMicrophoneTranscriber
    {
        private readonly ILogger _logger;
        private readonly int _microphoneIndex;
        private NetCoreAudioHandler _audioHandler;

        public NetCoreAudioMicrophoneTranscriber(ILogger logger, int microphoneIndex)
        {
            _logger = logger;
            _microphoneIndex = microphoneIndex;

            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Recordings");
            var di = Directory.CreateDirectory(workingDirectory);
            string wavOutputPath = Path.Combine(di.FullName, "recording-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");

            _audioHandler = new NetCoreAudioHandler(workingDirectory);
        }

        public async Task<FileInfo> TranscribeAudioAsync(
            WhisperProcessor processor,
            Func<bool> stopRecording,
            CancellationToken recordingToken,
            Func<bool> stopTranscription,
            CancellationToken transcriptionToken)
        {
            _logger.Information("Starting microphone recording...");
            await _audioHandler.Record();

            while (!recordingToken.IsCancellationRequested)
            {
                bool stop = stopRecording?.Invoke() ?? false;
                if (stop)
                {
                    _logger.Information("Recording stopped by user request.");
                    break;
                }
                await Task.Delay(100, recordingToken);
            }

            await _audioHandler.StopProcess();
            _logger.Information("Recording stopped. Transcribing...");

            string wavOutputPath = _audioHandler.GetLastRecordingPath();
            FileInfo wavFileInfo = new(wavOutputPath);

            var fileTranscriber = new FileTranscriber(_logger);
            var textFileInfo = await fileTranscriber.TranscribeAudioAsync(wavFileInfo, processor, transcriptionToken);

            while (!transcriptionToken.IsCancellationRequested)
            {
                bool stop = stopTranscription?.Invoke() ?? false;
                if (stop)
                {
                    _logger.Information("Transcription stopped by user request.");
                    break;
                }
                await Task.Delay(100, transcriptionToken);
            }

            return textFileInfo;
        }
    }
}
