using NUnit.Framework;
using Serilog;
using Serilog.Core;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class MediaFileScannerTests
    {
        private DirectoryInfo _directory = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "scan-tests", Guid.NewGuid().ToString("N")));
        }

        [TearDown]
        public void TearDown()
        {
            _directory.Delete(true);
        }

        [TestCase(OutputFormat.Srt, ".srt")]
        [TestCase(OutputFormat.Vtt, ".vtt")]
        [TestCase(OutputFormat.Txt, ".txt")]
        public async Task RecursiveScanSkipsExistingOutputsInTheSelectedFormat(OutputFormat format, string extension)
        {
            await CreateAsync("complete.MP3");
            await CreateAsync("complete" + extension);
            await CreateAsync("nested/complete.mp4");
            await CreateAsync("nested/complete" + extension);
            string pending = await CreateAsync("nested/pending.wav");

            Assert.That(Scan(true, format), Is.EqualTo(new[] { pending }));
        }

        [Test]
        public async Task OtherFormatsAndPartialFilesDoNotSkipPendingMedia()
        {
            string pending = await CreateAsync("recording.mp3");
            await CreateAsync("recording.srt");
            await CreateAsync("recording.txt.tmp");

            Assert.That(Scan(true, OutputFormat.Txt), Is.EqualTo(new[] { pending }));
        }

        [Test]
        public async Task NonrecursiveScanDoesNotIncludeSubfolders()
        {
            string pending = await CreateAsync("nested/recording.mp3");

            Assert.That(Scan(false, OutputFormat.Srt), Is.Empty);
            Assert.That(Scan(true, OutputFormat.Srt), Is.EqualTo(new[] { pending }));
        }

        [Test]
        public async Task OutputsInAnotherFolderDoNotSkipSameNamedMedia()
        {
            await CreateAsync("first/recording.mp3");
            await CreateAsync("first/recording.srt");
            string pending = await CreateAsync("second/recording.mp3");

            Assert.That(Scan(true, OutputFormat.Srt), Is.EqualTo(new[] { pending }));
        }

        [Test]
        public async Task TranscriptFilesAreNotQueuedAsMedia()
        {
            await CreateAsync("recording.srt");
            await CreateAsync("nested/recording.vtt");
            await CreateAsync("nested/recording.txt");

            Assert.That(Scan(true, OutputFormat.Srt), Is.Empty);
        }

        private string[] Scan(bool recursive, OutputFormat format)
        {
            using Logger logger = new LoggerConfiguration().CreateLogger();
            return MediaFileScanner.EnumeratePending(_directory, recursive, format, logger, CancellationToken.None)
                .Select(file => file.FullName).ToArray();
        }

        private async Task<string> CreateAsync(string relativePath)
        {
            string path = Path.GetFullPath(Path.Combine(_directory.FullName, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, string.Empty);
            return path;
        }
    }
}
