namespace WhisperCLI
{
    internal sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
}
