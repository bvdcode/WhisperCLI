using System.Text.RegularExpressions;

namespace WhisperCLI.Transcribers.Robust;

public static class TranscriptionQualityAnalyzer
{
    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)?", RegexOptions.Compiled);

    public static bool HasObviousLiveLoop(IReadOnlyList<TranscriptSegment> segments)
    {
        var nonEmpty = segments
            .Select(s => NormalizeText(s.Text))
            .Where(s => s.Length >= 4)
            .ToList();

        if (nonEmpty.Count >= 3)
        {
            var last = nonEmpty[^1];
            if (last == nonEmpty[^2] && last == nonEmpty[^3])
            {
                return true;
            }
        }

        var words = Tokenize(string.Join(' ', segments.Select(s => s.Text)));
        if (words.Count < 8)
        {
            return false;
        }

        // Look specifically at the tail. Whisper loops usually become highly periodic
        // and then continue indefinitely; aborting here saves most of the wasted decode.
        int maxPhrase = Math.Min(12, words.Count / 4);
        for (int phraseLength = 1; phraseLength <= maxPhrase; phraseLength++)
        {
            // Four repeated multi-word phrases are already highly suspicious. A single
            // repeated word ("yes yes yes...") needs a stronger signal to avoid aborting
            // legitimate emphatic speech/laughter.
            int repetitions = phraseLength == 1 ? 6 : 4;
            int needed = phraseLength * repetitions;
            if (needed > words.Count)
            {
                continue;
            }

            int start = words.Count - needed;
            bool same = true;
            for (int r = 1; r < repetitions && same; r++)
            {
                for (int j = 0; j < phraseLength; j++)
                {
                    if (!string.Equals(words[start + j], words[start + r * phraseLength + j], StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
            }

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    public static QualityAssessment Analyze(
        IReadOnlyList<TranscriptSegment> segments,
        TimeSpan coverageDuration,
        bool expectedSpeech,
        double suspiciousThreshold,
        bool liveLoopAborted = false)
    {
        var assessment = new QualityAssessment();
        var text = string.Join(' ', segments.Select(s => s.Text));
        var words = Tokenize(text);
        assessment.WordCount = words.Count;

        var normalizedSegments = segments
            .Select(s => NormalizeText(s.Text))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        assessment.MaxConsecutiveDuplicateSegments = MaxConsecutiveDuplicates(normalizedSegments);

        var repeat = FindStrongestConsecutivePhraseLoop(words);
        assessment.LongestRepeatedPhraseWords = repeat.PhraseLength;
        assessment.LongestRepeatedPhraseCount = repeat.Repetitions;
        assessment.RepeatedTokenFraction = words.Count == 0 ? 0 : (double)repeat.DuplicateTokens / words.Count;
        assessment.UniqueBigramRatio = CalculateUniqueBigramRatio(words);

        double seconds = Math.Max(coverageDuration.TotalSeconds, 0.1);
        int visibleCharacters = text.Count(c => !char.IsWhiteSpace(c));
        assessment.CharactersPerSecond = visibleCharacters / seconds;

        double score = 0;
        bool strongSignal = false;

        if (liveLoopAborted)
        {
            score = 1.0;
            strongSignal = true;
            assessment.Reasons.Add("decoder repetition loop detected while the attempt was still running");
        }

        if (expectedSpeech && words.Count == 0)
        {
            score += 0.85;
            strongSignal = true;
            assessment.Reasons.Add("VAD marked this chunk as speech but Whisper returned no words");
        }

        if (assessment.MaxConsecutiveDuplicateSegments >= 3)
        {
            score += 0.80;
            strongSignal = true;
            assessment.Reasons.Add($"same segment repeated {assessment.MaxConsecutiveDuplicateSegments} times consecutively");
        }

        if (repeat.Repetitions >= 4 && repeat.PhraseLength >= 1)
        {
            score += Math.Min(0.85, 0.35 + repeat.PhraseLength * 0.04 + (repeat.Repetitions - 4) * 0.05);
            strongSignal |= repeat.PhraseLength >= 2 || repeat.Repetitions >= 6;
            assessment.Reasons.Add($"{repeat.PhraseLength}-word phrase repeated {repeat.Repetitions} times consecutively");
        }
        else if (repeat.Repetitions >= 3 && repeat.PhraseLength >= 4)
        {
            score += 0.60;
            assessment.Reasons.Add($"{repeat.PhraseLength}-word phrase repeated {repeat.Repetitions} times consecutively");
        }

        if (assessment.RepeatedTokenFraction >= 0.60)
        {
            score += 0.35;
            assessment.Reasons.Add($"{assessment.RepeatedTokenFraction:P0} of words belong to one consecutive repetition run");
        }
        else if (assessment.RepeatedTokenFraction >= 0.35)
        {
            score += 0.20;
            assessment.Reasons.Add($"high repetition concentration ({assessment.RepeatedTokenFraction:P0})");
        }

        if (words.Count >= 50 && assessment.UniqueBigramRatio < 0.22)
        {
            score += 0.30;
            assessment.Reasons.Add($"very low lexical transition diversity (unique bigrams {assessment.UniqueBigramRatio:P0})");
        }

        if (visibleCharacters >= 250 && assessment.CharactersPerSecond > 45)
        {
            score += 0.25;
            assessment.Reasons.Add($"implausibly high output rate ({assessment.CharactersPerSecond:F1} chars/s)");
        }

        if (!AreTimestampsMonotonic(segments))
        {
            score += 0.50;
            assessment.Reasons.Add("segment timestamps are non-monotonic or invalid");
        }

        assessment.Score = Math.Clamp(score, 0, 1);
        assessment.Suspicious = strongSignal || assessment.Score >= suspiciousThreshold;
        return assessment;
    }

    private static bool AreTimestampsMonotonic(IReadOnlyList<TranscriptSegment> segments)
    {
        TimeSpan previousStart = TimeSpan.MinValue;
        foreach (var segment in segments)
        {
            if (segment.End < segment.Start || segment.Start < previousStart)
            {
                return false;
            }
            previousStart = segment.Start;
        }
        return true;
    }

    private static double CalculateUniqueBigramRatio(IReadOnlyList<string> words)
    {
        if (words.Count < 2)
        {
            return 1.0;
        }

        HashSet<string> bigrams = new(StringComparer.Ordinal);
        for (int i = 0; i < words.Count - 1; i++)
        {
            bigrams.Add(words[i] + "\u001F" + words[i + 1]);
        }

        return (double)bigrams.Count / (words.Count - 1);
    }

    private static int MaxConsecutiveDuplicates(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        int best = 1;
        int current = 1;
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] == values[i - 1])
            {
                current++;
                best = Math.Max(best, current);
            }
            else
            {
                current = 1;
            }
        }
        return best;
    }

    private static (int PhraseLength, int Repetitions, int DuplicateTokens) FindStrongestConsecutivePhraseLoop(IReadOnlyList<string> words)
    {
        int bestPhraseLength = 0;
        int bestRepetitions = 0;
        int bestDuplicateTokens = 0;

        int maxPhraseLength = Math.Min(12, words.Count / 3);
        for (int phraseLength = 1; phraseLength <= maxPhraseLength; phraseLength++)
        {
            for (int start = 0; start + phraseLength * 3 <= words.Count; start++)
            {
                int repetitions = 1;
                while (start + (repetitions + 1) * phraseLength <= words.Count &&
                       WindowsEqual(words, start, start + repetitions * phraseLength, phraseLength))
                {
                    repetitions++;
                }

                if (repetitions < 3)
                {
                    continue;
                }

                int duplicateTokens = (repetitions - 1) * phraseLength;
                if (duplicateTokens > bestDuplicateTokens ||
                    (duplicateTokens == bestDuplicateTokens && phraseLength > bestPhraseLength))
                {
                    bestPhraseLength = phraseLength;
                    bestRepetitions = repetitions;
                    bestDuplicateTokens = duplicateTokens;
                }
            }
        }

        return (bestPhraseLength, bestRepetitions, bestDuplicateTokens);
    }

    private static bool WindowsEqual(IReadOnlyList<string> words, int first, int second, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (!string.Equals(words[first + i], words[second + i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static List<string> Tokenize(string text)
    {
        return WordRegex.Matches(text.ToLowerInvariant())
            .Cast<Match>()
            .Select(m => m.Value)
            .ToList();
    }

    private static string NormalizeText(string text)
    {
        return string.Join(' ', Tokenize(text));
    }
}
