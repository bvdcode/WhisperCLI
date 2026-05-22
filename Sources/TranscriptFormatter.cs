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

        private static bool StartsWithPunctuation(string text)
        {
            char first = text[0];
            return first is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '…'
                or '。' or '，' or '、' or '！' or '？' or '；' or '：';
        }

        private static bool EndsWithSentencePunctuation(string text)
        {
            char last = text[^1];
            return last is '.' or '!' or '?' or '…' or '。' or '！' or '？';
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex MultipleSpacesRegex();

        [GeneratedRegex(@"\s+([,.;:!?…。，、！？；：])")]
        private static partial Regex SpaceBeforePunctuationRegex();
    }
}
