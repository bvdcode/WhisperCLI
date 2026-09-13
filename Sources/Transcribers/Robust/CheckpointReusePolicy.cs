namespace WhisperCLI.Transcribers.Robust;

/// <summary>Processing completion and transcription quality are independent.</summary>
public static class CheckpointReusePolicy
{
    public static bool IsTerminal(ChunkTranscriptionResult? result)
    {
        if (result is null || result.Chunk is null || result.Quality is null ||
            result.Attempts is null || result.SubChunks is null || result.Segments is null ||
            result.Chunk.Start < TimeSpan.Zero || result.Chunk.End <= result.Chunk.Start ||
            string.IsNullOrWhiteSpace(result.SelectedModel) ||
            string.IsNullOrWhiteSpace(result.SelectedStrategy) ||
            (result.Attempts.Count == 0 && result.SubChunks.Count == 0))
        {
            return false;
        }

        return result.Attempts.All(a => a is not null && string.IsNullOrWhiteSpace(a.Error)) &&
               result.Segments.All(s => s is not null && s.Text is not null &&
                   s.Start >= TimeSpan.Zero && s.End >= s.Start) &&
               (result.SubChunks.Count == 0 || CoversInterval(result.SubChunks, result.Chunk.Start, result.Chunk.End));
    }

    public static bool NeedsReview(ChunkTranscriptionResult result) =>
        result.NeedsReview || result.Quality.Suspicious || result.SubChunks.Any(NeedsReview);

    public static bool PropagateReviewFlags(ChunkTranscriptionResult result)
    {
        bool changed = false;
        foreach (ChunkTranscriptionResult child in result.SubChunks)
            changed |= PropagateReviewFlags(child);
        bool needsReview = NeedsReview(result);
        changed |= needsReview != result.NeedsReview;
        result.NeedsReview = needsReview;
        return changed;
    }

    public static bool CoversWholeAudio(IReadOnlyList<ChunkTranscriptionResult>? results, TimeSpan duration) =>
        CoversInterval(results, TimeSpan.Zero, duration);

    private static bool CoversInterval(IReadOnlyList<ChunkTranscriptionResult>? results, TimeSpan start, TimeSpan end)
    {
        if (results is null || results.Count == 0 || start < TimeSpan.Zero || end <= start)
            return false;
        TimeSpan cursor = start;
        foreach (ChunkTranscriptionResult result in results.OrderBy(c => c?.Chunk?.Start))
        {
            if (!IsTerminal(result) || Math.Abs((result.Chunk.Start - cursor).TotalMilliseconds) >= 5 ||
                result.Chunk.End > end + TimeSpan.FromMilliseconds(5))
                return false;
            cursor = result.Chunk.End;
        }
        return Math.Abs((cursor - end).TotalMilliseconds) < 5;
    }
}
