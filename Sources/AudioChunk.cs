namespace WhisperCLI
{
    internal readonly record struct AudioChunk(int Start, int End)
    {
        public int Length => End - Start;
    }
}
