using CommandLine;
using Whisper.net.Ggml;

namespace WhisperCLI
{
    public class AppOptions
    {
        [Option('m', "model", Required = false, Default = GgmlType.LargeV3Turbo,
            HelpText = "Primary Whisper model. Default: LargeV3Turbo.")]
        public GgmlType Model { get; set; }

        [Value(0, Required = false, HelpText = "Path to the input audio/video file. If omitted, microphone mode is used.")]
        public string InputFilePath { get; set; } = string.Empty;

        [Option("language", Required = false, Default = "auto",
            HelpText = "Whisper language code such as 'ru' or 'en'. Use 'auto' to auto-detect. For single-language recordings, explicitly specifying the language is more reliable.")]
        public string Language { get; set; } = "auto";

        [Option("fallback-models", Required = false, Default = "auto",
            HelpText = "Fallback models for suspicious chunks: 'auto', 'none', or a comma-separated list (for example LargeV3,LargeV2). Models are loaded lazily only when needed.")]
        public string FallbackModels { get; set; } = "auto";

        [Option("runtime", Required = false, Default = "auto",
            HelpText = "Whisper native runtime: auto, gpu, cuda, cuda12, or cpu. This build intentionally uses Whisper.net 1.8.1 to preserve the CUDA 12.1+ runtime that worked in the original application. cuda12 is an alias for cuda.")]
        public string Runtime { get; set; } = "auto";

        [Option("gpu-device", Required = false, Default = 0,
            HelpText = "GPU device index passed to Whisper. Default: 0.")]
        public int GpuDevice { get; set; } = 0;

        [Option("use-vad", Required = false, Default = true,
            HelpText = "Use the managed energy/silence detector to place coarse chunk boundaries at pauses. It is used only as a boundary hint; audio is never dropped because of it. Specify '--use-vad false' to disable. Default: true.")]
        public bool? UseVadOption { get; set; } = true;
        public bool UseVad => UseVadOption ?? true;

        [Option("chunk-seconds", Required = false, Default = 75,
            HelpText = "Target duration of independent long-file chunks in seconds. Default: 75.")]
        public int ChunkSeconds { get; set; } = 75;

        [Option("max-chunk-seconds", Required = false, Default = 90,
            HelpText = "Maximum chunk duration in seconds before forcing a boundary. Default: 90.")]
        public int MaxChunkSeconds { get; set; } = 90;

        [Option("max-context-tokens", Required = false, Default = 64,
            HelpText = "Maximum previous-text context tokens for the primary pass. Recovery passes disable previous-text context. Default: 64.")]
        public int MaxContextTokens { get; set; } = 64;

        [Option("entropy-threshold", Required = false, Default = 2.7f,
            HelpText = "Whisper entropy threshold used for decoding fallback. Default: 2.7.")]
        public float EntropyThreshold { get; set; } = 2.7f;

        [Option("temperature", Required = false, Default = 0.0f,
            HelpText = "Initial Whisper decoding temperature. Default: 0.0.")]
        public float Temperature { get; set; } = 0.0f;

        [Option("temperature-increment", Required = false, Default = 0.2f,
            HelpText = "Temperature increment used by Whisper fallback decoding. Default: 0.2.")]
        public float TemperatureIncrement { get; set; } = 0.2f;

        [Option("glitch-threshold", Required = false, Default = 0.65,
            HelpText = "Automatic repetition/glitch score threshold in the range 0..1. Default: 0.65.")]
        public double GlitchThreshold { get; set; } = 0.65;

        [Option("max-recovery-depth", Required = false, Default = 2,
            HelpText = "Maximum recursive chunk splitting depth after direct retries fail. Default: 2.")]
        public int MaxRecoveryDepth { get; set; } = 2;

        [Option("min-recovery-split-seconds", Required = false, Default = 15,
            HelpText = "Minimum child duration when recursively splitting a problematic chunk. Default: 15 seconds.")]
        public int MinRecoverySplitSeconds { get; set; } = 15;

        [Option("resume", Required = false, Default = true,
            HelpText = "Resume completed chunks from a matching checkpoint after interruption. Specify '--resume false' for a fresh run. Default: true.")]
        public bool? ResumeOption { get; set; } = true;
        public bool Resume => ResumeOption ?? true;

        [Option("lock-detected-language", Required = false, Default = true,
            HelpText = "When --language auto is used, lock the first reliable detected language for later chunks. Specify false to disable. Default: true.")]
        public bool? LockDetectedLanguageOption { get; set; } = true;
        public bool LockDetectedLanguage => LockDetectedLanguageOption ?? true;

        [Option("vad-threshold", Required = false, Default = 0.5f,
            HelpText = "Managed silence detector sensitivity in the range 0..1. Higher values require more energy above the estimated noise floor. Default: 0.5.")]
        public float VadThreshold { get; set; } = 0.5f;

        [Option("vad-min-speech-ms", Required = false, Default = 250,
            HelpText = "Minimum detected active-audio duration in milliseconds. Default: 250.")]
        public int VadMinSpeechMs { get; set; } = 250;

        [Option("vad-min-silence-ms", Required = false, Default = 700,
            HelpText = "Minimum quiet gap used to close an active-audio region, in milliseconds. Default: 700.")]
        public int VadMinSilenceMs { get; set; } = 700;

        [Option("vad-speech-padding-ms", Required = false, Default = 250,
            HelpText = "Padding applied around detected active-audio regions, in milliseconds. Default: 250.")]
        public int VadSpeechPaddingMs { get; set; } = 250;

        [Option("vad-edge-padding-ms", Required = false, Default = 150,
            HelpText = "Safety margin kept away from detected activity when choosing a chunk boundary, in milliseconds. Default: 150.")]
        public int VadEdgePaddingMs { get; set; } = 150;

        [Option('i', "microphone-index", Required = false, Default = 0,
            HelpText = "Index of the microphone to use for recording. Default: 0.")]
        public int MicrophoneIndex { get; set; }

        [Option('s', "stop-key", Required = false, Default = ConsoleKey.Spacebar,
            HelpText = "Key to stop microphone recording. Default: Spacebar.")]
        public ConsoleKey StopKey { get; set; }

        [Option('o', "open-results", Required = false, Default = false,
            HelpText = "Open the resulting text file after transcription.")]
        public bool OpenTextFile { get; set; }

        [Option('c', "copy-to-clipboard", Required = false, Default = true,
            HelpText = "Copy the transcription result to the clipboard. Specify '-c false' or '--copy-to-clipboard false' to disable. Default: true.")]
        public bool? CopyToClipboardOption { get; set; } = true;
        public bool CopyToClipboard => CopyToClipboardOption ?? true;

        [Option('d', "delay-seconds", Required = false, Default = 10,
            HelpText = "Delay before closing the application. Default: 10 seconds.")]
        public int DelaySeconds { get; set; } = 10;

        [Option('l', "lockfile", Required = false, Default = true,
            HelpText = "Prevent simultaneous WhisperCLI instances. Specify '-l false' or '--lockfile false' to disable. Default: true.")]
        public bool? UseLockfileOption { get; set; } = true;
        public bool UseLockfile => UseLockfileOption ?? true;

        [Option('v', "verbose", Required = false, Default = false,
            HelpText = "Enable verbose logging.")]
        public bool Verbose { get; set; }
    }
}
