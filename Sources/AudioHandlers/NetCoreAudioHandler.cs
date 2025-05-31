using NetCoreAudio.Interfaces;
using NetCoreAudio.Players;
using NetCoreAudio.Recorders;
using NetCoreAudio;


namespace WhisperCLI.AudioHandlers
{
    public class NetCoreAudioHandler
    {
        private readonly Player _audioPlayer;
        private readonly Recorder _audioRecorder;
        private readonly string _directoryPath;
        private readonly string _lastRecordingPath;
        private readonly string newLine = Environment.NewLine;
        public TaskCompletionSource<bool>? PlaybackFinishedTcs;

        public NetCoreAudioHandler(string directoryPath)
        {
            _directoryPath = directoryPath;

            _lastRecordingPath = Path.Combine(_directoryPath, "last_recording.wav");

            _audioPlayer = new Player();
            _audioPlayer.PlaybackFinished += (sender, e) => PlaybackFinishedTcs!.SetResult(true);
            _audioRecorder = new Recorder();
        }

        public void Stop()
        {
            // Stop all processes
            StopProcess().Wait();

            // Remove file
            if (System.IO.File.Exists(_lastRecordingPath))
            {
                System.IO.File.Delete(_lastRecordingPath);
            }
        }

        public async Task Record()
        {
            await _audioRecorder.Record(_lastRecordingPath);
        }

        public async Task StopProcess()
        {
            if (_audioRecorder.Recording)
            {
                await _audioRecorder.Stop();
            }
            if (_audioPlayer.Playing)
            {
                await _audioPlayer.Stop();
            }
        }

        public async Task Play()
        {
            PlaybackFinishedTcs = new TaskCompletionSource<bool>();
            await _audioPlayer.Play(_lastRecordingPath);
            // await PlaybackFinishedTcs.Task;
        }

        private void OnPlaybackFinished(object sender, EventArgs e)
        {
            Console.WriteLine("Playback finished.");
        }
    }
}