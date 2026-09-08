namespace WhisperCLI.Transcribers.Robust;

public static class ChunkPlanner
{
    /// <summary>
    /// Builds coarse chunks that cover the complete recording timeline while
    /// using VAD silence as a boundary hint. VAD is deliberately NOT used to concatenate
    /// tiny speech islands: doing that destroys natural context and makes timestamp
    /// handling harder. Pure-silence chunks are retained with ExpectedSpeech=false so
    /// the caller can skip Whisper for them without losing timeline diagnostics.
    /// </summary>
    public static List<AudioChunk> FromVad(
        IReadOnlyList<SpeechRegion> speechRegions,
        TimeSpan audioDuration,
        TimeSpan targetDuration,
        TimeSpan maxDuration,
        TimeSpan edgePadding)
    {
        var regions = speechRegions
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start)
            .ToList();

        if (regions.Count == 0)
        {
            return [];
        }

        // Cover the full timeline, not only [first VAD speech, last VAD speech]. This is
        // important because VAD can miss a quiet phrase near the beginning/end; if that
        // phrase shares a coarse chunk with detected speech, Whisper still gets to see it.
        // Pure-silence chunks are cheaply skipped later.
        TimeSpan activeStart = TimeSpan.Zero;
        TimeSpan activeEnd = audioDuration;
        if (activeEnd <= activeStart)
        {
            return [];
        }

        // Do not create pathological tiny chunks merely because the ideal target happens
        // to sit just after a silence. This is only a lower preference bound; the final
        // tail may of course be shorter.
        TimeSpan minimumPreferred = TimeSpan.FromSeconds(Math.Min(20, targetDuration.TotalSeconds / 3.0));
        List<AudioChunk> chunks = [];
        TimeSpan start = activeStart;

        while (start < activeEnd)
        {
            TimeSpan remaining = activeEnd - start;
            TimeSpan end;

            if (remaining <= maxDuration)
            {
                end = activeEnd;
            }
            else
            {
                TimeSpan desired = Min(activeEnd, start + targetDuration);
                TimeSpan latest = Min(activeEnd, start + maxDuration);
                TimeSpan earliest = Min(latest, start + minimumPreferred);
                end = FindBestSilenceBoundary(regions, earliest, desired, latest, edgePadding)
                      ?? latest;
            }

            if (end <= start)
            {
                // Defensive progress guarantee for unusual timestamp/rounding input.
                end = Min(activeEnd, start + maxDuration);
                if (end <= start)
                {
                    break;
                }
            }

            chunks.Add(new AudioChunk
            {
                Id = (chunks.Count + 1).ToString("D4"),
                Start = start,
                End = end,
                ExpectedSpeech = ContainsSpeech(regions, start, end)
            });
            start = end;
        }

        // A tiny tail is better merged with its predecessor when that does not violate
        // the hard maximum. This avoids wasting a full Whisper invocation on a few seconds.
        if (chunks.Count >= 2)
        {
            AudioChunk last = chunks[^1];
            AudioChunk previous = chunks[^2];
            if (last.Duration < TimeSpan.FromSeconds(12) && last.End - previous.Start <= maxDuration)
            {
                previous.End = last.End;
                previous.ExpectedSpeech |= last.ExpectedSpeech;
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Id = (i + 1).ToString("D4");
        }

        return chunks;
    }

    public static List<AudioChunk> Fixed(TimeSpan audioDuration, TimeSpan targetDuration)
    {
        List<AudioChunk> chunks = [];
        TimeSpan start = TimeSpan.Zero;
        while (start < audioDuration)
        {
            TimeSpan end = Min(audioDuration, start + targetDuration);
            chunks.Add(new AudioChunk
            {
                Id = (chunks.Count + 1).ToString("D4"),
                Start = start,
                End = end,
                // Without VAD we do not know whether speech is expected; false means an
                // empty result by itself is not treated as a transcription failure.
                ExpectedSpeech = false
            });
            start = end;
        }
        return chunks;
    }

    public static TimeSpan FindRecoverySplitPoint(
        AudioChunk chunk,
        IReadOnlyList<SpeechRegion> speechRegions,
        TimeSpan minChildDuration)
    {
        TimeSpan midpoint = chunk.Start + TimeSpan.FromTicks(chunk.Duration.Ticks / 2);
        TimeSpan min = chunk.Start + minChildDuration;
        TimeSpan max = chunk.End - minChildDuration;

        if (min >= max)
        {
            return midpoint;
        }

        TimeSpan? best = null;
        TimeSpan bestDistance = TimeSpan.MaxValue;

        for (int i = 0; i < speechRegions.Count - 1; i++)
        {
            SpeechRegion left = speechRegions[i];
            SpeechRegion right = speechRegions[i + 1];
            if (left.End <= chunk.Start || right.Start >= chunk.End)
            {
                continue;
            }

            TimeSpan gap = right.Start - left.End;
            if (gap < TimeSpan.FromMilliseconds(250))
            {
                continue;
            }

            TimeSpan candidate = Clamp(midpoint, Max(min, left.End), Min(max, right.Start));
            if (candidate < min || candidate > max || candidate < left.End || candidate > right.Start)
            {
                continue;
            }

            TimeSpan distance = (candidate - midpoint).Duration();
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best ?? Clamp(midpoint, min, max);
    }

    private static TimeSpan? FindBestSilenceBoundary(
        IReadOnlyList<SpeechRegion> regions,
        TimeSpan earliest,
        TimeSpan desired,
        TimeSpan latest,
        TimeSpan safetyMargin)
    {
        TimeSpan? best = null;
        TimeSpan bestDistance = TimeSpan.MaxValue;

        for (int i = 0; i < regions.Count - 1; i++)
        {
            TimeSpan gapStart = regions[i].End + safetyMargin;
            TimeSpan gapEnd = regions[i + 1].Start - safetyMargin;
            if (gapEnd <= gapStart)
            {
                continue;
            }

            TimeSpan usableStart = Max(earliest, gapStart);
            TimeSpan usableEnd = Min(latest, gapEnd);
            if (usableEnd < usableStart)
            {
                continue;
            }

            // Any point inside a sufficiently quiet gap is safe. Pick the point closest
            // to the target duration rather than always taking the middle of a long pause.
            TimeSpan candidate = Clamp(desired, usableStart, usableEnd);
            TimeSpan distance = (candidate - desired).Duration();
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool ContainsSpeech(IReadOnlyList<SpeechRegion> regions, TimeSpan start, TimeSpan end)
    {
        foreach (SpeechRegion region in regions)
        {
            if (region.End <= start)
            {
                continue;
            }
            if (region.Start >= end)
            {
                break;
            }
            return true;
        }
        return false;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
