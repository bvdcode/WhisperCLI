using CommandLine;
using Whisper.net.Ggml;

namespace WhisperCLI
{
    public class AppOptions
    {
        [Option('m', "model", Required = false, Default = GgmlType.LargeV3Turbo,
            HelpText = "Model to use for transcription. Default is 'large-v3-turbo'. Available models: " +
            "'tiny', 'base', 'small', 'medium', 'large-v1', 'large-v2', 'large-v3-turbo'.")]
        public GgmlType Model { get; set; }

        [Value(1, Required = false, HelpText = "Path to the input audio file to transcribe.")]
        public string InputFilePath { get; set; } = string.Empty;

        [Option('i', "microphone-index", Required = false, Default = 0, HelpText = "Index of the microphone to use for recording. Default is 0 (first microphone).")]
        public int MicrophoneIndex { get; set; }

        [Option('s', "stop-key", Required = false, Default = ConsoleKey.Spacebar, HelpText = "Key to stop recording when using microphone input. Default is 'Space'.")]
        public ConsoleKey StopKey { get; set; }

        [Option('o', "open-results", Required = false, Default = false, HelpText = "Open the results text file after transcription.")]
        public bool OpenTextFile { get; set; }

        [Option('c', "copy-to-clipboard", Required = false, Default = true, HelpText = "Copy the transcription result to the clipboard.")]
        public bool CopyToClipboard { get; set; }

        [Option('d', "delay-seconds", Required = false, Default = 10, HelpText = "Delay in seconds after transcription before closing the application. Default is 10 seconds.")]
        public int DelaySeconds { get; set; }

        [Option("lockfile", Required = false, Default = true, HelpText = "Use a lockfile to prevent multiple instances from running simultaneously. Default is true.")]
        public bool UseLockfile { get; set; }

        [Option('v', "verbose", Required = false, Default = false, HelpText = "Enable verbose logging.")]
        public bool Verbose { get; set; }

        [Option('l', "language", Required = false, Default = "auto", HelpText = "Language of the audio input. Default is 'auto'. Specify a language code (e.g., 'en' for English) to force a specific language.")]
        public string Language { get; set; } = "auto";
    }
}