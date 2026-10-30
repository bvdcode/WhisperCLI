using System.Runtime.CompilerServices;
using Serilog;
using Whisper.net;
using Whisper.net.Wave;

namespace WhisperCLI
{
    internal static class SpeechTranscriber
    {
        public static async IAsyncEnumerable<TranscriptSegment> ProcessAsync(
            Stream waveStream,
            WhisperProcessor processor,
            WhisperVadProcessor? vad,
            ILogger logger,
            [EnumeratorCancellation] CancellationToken token,
            bool detectLanguage = false)
        {
            WaveParser parser = new(waveStream);
            float[] samples = await parser.GetAvgSamplesAsync(token).ConfigureAwait(false);
            if (samples.Length == 0)
            {
                yield break;
            }

            if (parser.SampleRate != SpeechChunker.SampleRate)
            {
                throw new InvalidOperationException("Transcription requires 16 kHz audio.");
            }

            IEnumerable<AudioChunk> speech = [new(0, samples.Length)];
            if (vad != null)
            {
                logger.Information("Detecting speech before transcription...");
                IReadOnlyList<VadSegmentData> detected = await vad.DetectSpeechAsync(samples, token).ConfigureAwait(false);
                speech = detected.Select(s => new AudioChunk(
                    (int)(s.Start.TotalSeconds * SpeechChunker.SampleRate),
                    (int)(s.End.TotalSeconds * SpeechChunker.SampleRate)));
            }

            IReadOnlyList<AudioChunk> chunks = SpeechChunker.Create(samples.Length, speech);
            if (detectLanguage && chunks.Count > 0)
            {
                // Detect once on a substantial speech region, not again on every short reply.
                token.ThrowIfCancellationRequested();
                AudioChunk languageSample = chunks.FirstOrDefault(c => c.Length >= 10 * SpeechChunker.SampleRate);
                if (languageSample.Length == 0)
                {
                    languageSample = chunks.MaxBy(c => c.Length);
                }
                int languageSampleLength = Math.Min(languageSample.Length, 30 * SpeechChunker.SampleRate);
                string? language = processor.DetectLanguage(samples.AsSpan(languageSample.Start, languageSampleLength).ToArray());
                processor.ChangeLanguage(language ?? "auto");
                logger.Information("Detected recording language: {language}", language ?? "auto");
            }
            logger.Information("Transcribing {count} speech regions; continuous speech is passed intact to Whisper.", chunks.Count);
            foreach (AudioChunk chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                ReadOnlyMemory<float> audio = samples.AsMemory(chunk.Start, chunk.Length);
                if (chunk.Length < SpeechChunker.SampleRate)
                {
                    // The native decoder otherwise skips input shorter than one second.
                    float[] padded = new float[SpeechChunker.SampleRate];
                    audio.CopyTo(padded);
                    audio = padded;
                }

                TimeSpan offset = TimeSpan.FromSeconds(chunk.Start / (double)SpeechChunker.SampleRate);
                TimeSpan duration = TimeSpan.FromSeconds(chunk.Length / (double)SpeechChunker.SampleRate);
                await foreach (SegmentData result in processor.ProcessAsync(audio, token).ConfigureAwait(false))
                {
                    if (TranscriptPhraseFilter.IsExcluded(result.Text) || string.IsNullOrWhiteSpace(result.Text))
                    {
                        logger.Debug("Skipping excluded or empty transcript phrase: {text}", result.Text);
                        continue;
                    }

                    TimeSpan start = offset + Clamp(result.Start, duration);
                    TimeSpan end = offset + Clamp(result.End, duration);
                    if (end < start)
                    {
                        end = start;
                    }

                    logger.Information("{lang}: {start}->{end}: {text}", result.Language,
                        start.ToString(@"hh\:mm\:ss"), end.ToString(@"hh\:mm\:ss"), result.Text);
                    yield return new TranscriptSegment(start, end, result.Text);
                }
            }
        }

        private static TimeSpan Clamp(TimeSpan value, TimeSpan duration)
        {
            return value < TimeSpan.Zero ? TimeSpan.Zero : value > duration ? duration : value;
        }
    }
}
