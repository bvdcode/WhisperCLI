namespace WhisperCLI
{
    internal static class SpeechChunker
    {
        internal const int SampleRate = 16000;
        internal const int MaxChunkSamples = 30 * SampleRate;

        // Keep the original sample positions: removing silence must not shift subtitle timestamps.
        public static IReadOnlyList<AudioChunk> Create(int sampleCount, IEnumerable<AudioChunk> speech)
        {
            List<AudioChunk> chunks = [];
            foreach (AudioChunk range in speech.OrderBy(s => s.Start))
            {
                int start = Math.Clamp(range.Start, 0, sampleCount);
                int end = Math.Clamp(range.End, start, sampleCount);
                if (start == end)
                {
                    continue;
                }

                // Whisper ignores audio shorter than one second. Retain short replies with context.
                if (end - start < SampleRate)
                {
                    int extra = SampleRate - (end - start);
                    start = Math.Max(0, start - extra / 2);
                    end = Math.Min(sampleCount, start + SampleRate);
                    start = Math.Max(0, end - SampleRate);
                }

                if (chunks.Count > 0)
                {
                    AudioChunk previous = chunks[^1];
                    if (start - previous.End <= SampleRate && end - previous.Start <= MaxChunkSamples)
                    {
                        chunks[^1] = previous with { End = Math.Max(previous.End, end) };
                        continue;
                    }

                    // VAD padding may overlap at a forced split. Decode that audio only once.
                    start = Math.Max(start, previous.End);
                }

                while (start < end)
                {
                    int chunkEnd = Math.Min(end, start + MaxChunkSamples);
                    chunks.Add(new AudioChunk(start, chunkEnd));
                    start = chunkEnd;
                }
            }

            return chunks;
        }
    }
}
