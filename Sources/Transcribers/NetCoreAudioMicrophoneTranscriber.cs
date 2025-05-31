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

        public async Task<FileInfo> TranscribeAudioAsync(WhisperProcessor processor, Func<bool> stopRecording, CancellationToken token)
        {
            _logger.Information("Starting microphone recording...");

            await _audioHandler.Record();

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
            await _audioHandler.StopProcess();

            _logger.Information("Recording stopped. Transcribing...");

            string wavOutputPath = _audioHandler.GetLastRecordingPath();
            
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
