using NUnit.Framework;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class SpeechChunkerTests
    {
        private const int Rate = 16000;

        [Test]
        public void LongSilenceKeepsTheOriginalTimeline()
        {
            var chunks = SpeechChunker.Create(400 * Rate, [new(10 * Rate, 20 * Rate), new(300 * Rate, 310 * Rate)]);
            Assert.That(chunks, Is.EqualTo(new AudioChunk[] { new(10 * Rate, 20 * Rate), new(300 * Rate, 310 * Rate) }));
        }

        [Test]
        public void NearbySpeechStaysIntactBeyondThirtySeconds()
        {
            var chunks = SpeechChunker.Create(60 * Rate, [new(0, 10 * Rate), new(11 * Rate, 20 * Rate), new(21 * Rate, 40 * Rate)]);
            Assert.That(chunks, Is.EqualTo(new AudioChunk[] { new(0, 40 * Rate) }));
        }

        [Test]
        public void OverlappingSpeechIsMergedWithoutCuttingEitherRegion()
        {
            var chunks = SpeechChunker.Create(60 * Rate, [new(0, 29 * Rate), new(28 * Rate, 55 * Rate)]);
            Assert.That(chunks, Is.EqualTo(new AudioChunk[] { new(0, 55 * Rate) }));
        }

        [Test]
        public void ShortReplyGetsAtLeastOneSecondOfAudio()
        {
            var chunks = SpeechChunker.Create(10 * Rate, [new(2 * Rate, 2 * Rate + 3200)]);
            Assert.That(chunks, Is.EqualTo(new AudioChunk[] { new(25600, 41600) }));
        }

        [Test]
        public void SpeechAtTheEndIsClampedAndRetained()
        {
            var chunks = SpeechChunker.Create(10 * Rate, [new(10 * Rate - 1600, 11 * Rate)]);
            Assert.That(chunks, Is.EqualTo(new AudioChunk[] { new(9 * Rate, 10 * Rate) }));
        }

        [Test]
        public void NoDetectedSpeechProducesNoChunks()
        {
            Assert.That(SpeechChunker.Create(60 * Rate, []), Is.Empty);
        }

        [Test]
        public void UnfilteredAudioIsPassedWholeWithoutArtificialBoundaries()
        {
            var chunks = SpeechChunker.Create(65 * Rate, [new(0, 65 * Rate)]);
            Assert.That(chunks, Is.EqualTo(new AudioChunk[] { new(0, 65 * Rate) }));
        }
    }
}
