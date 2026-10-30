using System.Text.RegularExpressions;

namespace WhisperCLI
{
    internal static partial class TranscriptPhraseFilter
    {
        private static readonly HashSet<string> ExcludedPhrases = new(StringComparer.OrdinalIgnoreCase)
        {
            "Продолжение следует",
            "Субтитры создавал DimaTorzok",
            "Субтитры сделал DimaTorzok",
            "Субтитры делал DimaTorzok",
            "Редактор субтитров А Семкин Корректор А Егорова"
        };

        public static bool IsExcluded(string text)
        {
            string normalized = PhraseSeparatorsRegex().Replace(text, " ").Trim();
            return ExcludedPhrases.Contains(normalized);
        }

        [GeneratedRegex(@"[\p{P}\s]+")]
        private static partial Regex PhraseSeparatorsRegex();
    }
}
