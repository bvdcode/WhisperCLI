using Serilog.Core;
using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperCLI
{
    public static class WhisperProcessorFactory
    {
        public static WhisperProcessor Create(FileInfo whisperModelInfo, string language, Logger logger, GgmlType model, int beamSize = 5)
        {
            WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelInfo.FullName);
            logger.Information("WhisperProcessor loaded in background: {model}", model);
            WhisperProcessorBuilder builder = whisperFactory
                .CreateBuilder()
                .WithLanguage(language)
                .WithNoContext()
                // NoContext alone does not disable rolling text history within a processing call.
                .WithMaxLastTextTokens(0)
                .WithTemperature(0f);
            if (beamSize > 1)
            {
                builder.WithBeamSearchSamplingStrategy(sampling => sampling.WithBeamSize(beamSize));
            }
            // Whisper treats prompts as transcript context and can copy instructions into speech.
            return builder.Build();
        }

        public static WhisperVadProcessor CreateVad(FileInfo model, float threshold, Logger logger)
        {
            using WhisperVadFactory factory = WhisperVadFactory.FromPath(model.FullName);
            logger.Information("Using Silero VAD with speech threshold {threshold}.", threshold);
            return factory.CreateBuilder()
                .WithUseGpu(false)
                .WithThreshold(threshold)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(500))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(250))
                .Build();
        }
    }
}
