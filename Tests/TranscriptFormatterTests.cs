using NUnit.Framework;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class TranscriptFormatterTests
    {
        [TestCase(OutputFormat.Srt)]
        [TestCase(OutputFormat.Vtt)]
        public void SubtitleFragmentsPreserveSentencePunctuation(OutputFormat format)
        {
            TranscriptSegment[] segments =
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "  Если  вы приехали , "),
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "мы обсудим вопрос."),
            ];

            string output = TranscriptFormatter.Format(segments, format);

            Assert.That(output, Does.Contain("Если вы приехали,"));
            Assert.That(output, Does.Not.Contain(",."));
            Assert.That(output, Does.Contain("мы обсудим вопрос."));
        }

        [TestCase(OutputFormat.Srt)]
        [TestCase(OutputFormat.Vtt)]
        public void RepeatedSpeechAtDifferentTimesIsPreserved(OutputFormat format)
        {
            TranscriptSegment[] segments =
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Доброе утро."),
                new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "Доброе утро."),
            ];

            string output = TranscriptFormatter.Format(segments, format);

            Assert.That(output.Split("Доброе утро.", StringSplitOptions.None), Has.Length.EqualTo(3));
        }

        [Test]
        public void PlainTextCompletesTheSentenceAfterCombiningFragments()
        {
            TranscriptSegment[] segments =
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "  Если  вы приехали , "),
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "мы обсудим вопрос"),
            ];

            string output = TranscriptFormatter.Format(segments, OutputFormat.Txt);

            Assert.That(output, Is.EqualTo("Если вы приехали, мы обсудим вопрос."));
        }
    }
}
