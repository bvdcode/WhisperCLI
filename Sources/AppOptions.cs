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

        [Value(1, Required = true, HelpText = "Path to the input audio file to transcribe.")]
        public string InputFilePath { get; set; } = string.Empty;
    }
}