using System;
using System.Threading.Tasks;
using NetCoreAudio.Interfaces;
using NetCoreAudio.Players;
using NetCoreAudio.Recorders;
using NetCoreAudio;
using Bagheera.Transcriber;
using Bagheera.Transcriber.Models;


namespace Bagheera.Core
{
    public class AudioHandler
    {
        private readonly Player _audioPlayer;
        private readonly Recorder _audioRecorder;
        private readonly WhisperTranscriber _transcriber;
        private readonly string _directoryPath;
        private readonly string _lastRecordingPath;
        private readonly string newLine = Environment.NewLine;
        public bool Recording { get; private set; }
        public bool Playing { get; private set; }
        public bool Transcribing { get; private set; }
        public TaskCompletionSource<bool>? PlaybackFinishedTcs;

        public AudioHandler(string directoryPath)
        {
            _directoryPath = directoryPath;

            _lastRecordingPath = Path.Combine(_directoryPath, "last_recording.wav");

            _audioPlayer = new Player();
            _audioPlayer.PlaybackFinished += (sender, e) => PlaybackFinishedTcs!.SetResult(true);
            _audioRecorder = new Recorder();
            _transcriber = new WhisperTranscriber(_directoryPath);
        }
        public string ListAllModels()
        {
            return _transcriber.WhisperModelManager.GetModelsInfo();
        }

        public string GetCurrentModelInfo()
        {
            return _transcriber.Model.ToString();
        }

        public string GetCurrentModelName()
        {
            return _transcriber.Model.Name;
        }

        public string GetModelSizeByModelName(string modelName)
        {
            return _transcriber.WhisperModelManager.ModelList.Find(x => x.Name == modelName)!.Size;
        }

        public bool IsModelInstalled(string modelName)
        {
            return _transcriber.WhisperModelManager.ModelList.Find(x => x.Name == modelName)!.IsInstalled;
        }

        public bool ModelExists(string modelName)
        {
            if (_transcriber.WhisperModelManager.ModelList.Find(x => x.Name == modelName) != null)
            {
                return true;
            }
            return false;
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

        public async Task<string> Transcribe()
        {
            var result = await _transcriber.TranscribeAsync(_lastRecordingPath);
            return result;
        }

        public void SetModel(string modelName)
        {
            _transcriber.SetModel(modelName);
        }

        public async Task InstallModel(string modelName)
        {
            await _transcriber.InstallModel(modelName);
        }

        public void InstallAllModels()
        {
            _transcriber.InstallAllModels();
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
            if (_transcriber.Transcribing)
            {
                await _transcriber.Stop();
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