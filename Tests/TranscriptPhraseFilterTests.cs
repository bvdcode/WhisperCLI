using NUnit.Framework;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class TranscriptPhraseFilterTests
    {
        [TestCase("Продолжение следует")]
        [TestCase("  Продолжение следует...  ")]
        [TestCase("ПРОДОЛЖЕНИЕ СЛЕДУЕТ!")]
        [TestCase("Продолжение следует…")]
        [TestCase("«Продолжение следует». ")]
        [TestCase("Продолжение\n\tследует....")]
        public void StandaloneExcludedPhraseIsRecognized(string text)
        {
            Assert.That(TranscriptPhraseFilter.IsExcluded(text), Is.True);
        }

        [TestCase("Мы обсудим, когда продолжение следует ожидать.")]
        [TestCase("В конце фильма написано: продолжение следует.")]
        [TestCase("Продолжение следует завтра.")]
        [TestCase("Доброе утро.")]
        [TestCase("Спасибо за внимание.")]
        [TestCase("")]
        [TestCase("...")]
        public void OtherSpeechIsPreserved(string text)
        {
            Assert.That(TranscriptPhraseFilter.IsExcluded(text), Is.False);
        }
    }
}
