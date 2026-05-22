using System.Text;
using System.Text.RegularExpressions;

namespace WhisperCLI
{
    internal static partial class TranscriptFormatter
    {
        public static void AppendSegment(StringBuilder builder, string text)
        {
            string segment = text.Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                return;
            }

            if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && !StartsWithPunctuation(segment))
            {
                builder.Append(' ');
            }

            builder.Append(segment);
        }

        public static string Finalize(string text)
        {
            string normalized = MultipleSpacesRegex().Replace(text.Trim(), " ");
            normalized = SpaceBeforePunctuationRegex().Replace(normalized, "$1");

            if (normalized.Length == 0 || EndsWithSentencePunctuation(normalized))
            {
                return normalized;
            }

            return normalized + ".";
        }

        public static string Format(IReadOnlyList<TranscriptSegment> segments, OutputFormat format)
        {
            return format switch
            {
                OutputFormat.Txt => FormatText(segments),
                OutputFormat.Vtt => FormatWebVtt(segments),
                _ => FormatSrt(segments)
            };
        }

        public static string GetFileExtension(OutputFormat format)
        {
            return format switch
            {
                OutputFormat.Txt => ".txt",
                OutputFormat.Vtt => ".vtt",
                _ => ".srt"
            };
        }

        private static string FormatText(IReadOnlyList<TranscriptSegment> segments)
        {
            StringBuilder builder = new();
            foreach (TranscriptSegment segment in segments)
            {
                AppendSegment(builder, segment.Text);
            }

            return Finalize(builder.ToString());
        }

        private static string FormatSrt(IReadOnlyList<TranscriptSegment> segments)
        {
            StringBuilder builder = new();
            for (int i = 0; i < segments.Count; i++)
            {
                TranscriptSegment segment = segments[i];
                builder.AppendLine((i + 1).ToString());
                builder.Append(FormatSrtTimestamp(segment.Start));
                builder.Append(" --> ");
                builder.AppendLine(FormatSrtTimestamp(segment.End));
                builder.AppendLine(Finalize(segment.Text));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string FormatWebVtt(IReadOnlyList<TranscriptSegment> segments)
        {
            StringBuilder builder = new();
            builder.AppendLine("WEBVTT");
            builder.AppendLine();

            foreach (TranscriptSegment segment in segments)
            {
                builder.Append(FormatWebVttTimestamp(segment.Start));
                builder.Append(" --> ");
                builder.AppendLine(FormatWebVttTimestamp(segment.End));
                builder.AppendLine(Finalize(segment.Text));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string FormatSrtTimestamp(TimeSpan value)
        {
            return value.ToString(@"hh\:mm\:ss\,fff");
        }

        private static string FormatWebVttTimestamp(TimeSpan value)
        {
            return value.ToString(@"hh\:mm\:ss\.fff");
        }

        private static bool StartsWithPunctuation(string text)
        {
            char first = text[0];
            return first is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '\u2026'
                or '\u3002' or '\uff0c' or '\u3001' or '\uff01' or '\uff1f' or '\uff1b' or '\uff1a';
        }

        private static bool EndsWithSentencePunctuation(string text)
        {
            char last = text[^1];
            return last is '.' or '!' or '?' or '\u2026' or '\u3002' or '\uff01' or '\uff1f';
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex MultipleSpacesRegex();

        [GeneratedRegex(@"\s+([,.;:!?\u2026\u3002\uff0c\u3001\uff01\uff1f\uff1b\uff1a])")]
        private static partial Regex SpaceBeforePunctuationRegex();
    }
}
