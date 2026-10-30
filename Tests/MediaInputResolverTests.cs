using NUnit.Framework;
using Serilog;
using Serilog.Core;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class MediaInputResolverTests
    {
        private string _directory = string.Empty;
        private Logger _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "input-tests", Guid.NewGuid().ToString("N"))).FullName;
            _logger = new LoggerConfiguration().CreateLogger();
        }

        [TearDown]
        public void TearDown()
        {
            _logger.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task OmittedInputSelectsMicrophoneMode()
        {
            TranscriptionInput? input = await ResolveAsync(new AppOptions());

            Assert.That(input, Is.Not.Null);
            Assert.That(input!.Files, Is.Empty);
            Assert.That(input.FolderMode, Is.False);
        }

        [Test]
        public async Task ExplicitFileIsSelectedEvenWhenTranscriptExists()
        {
            string file = Path.Combine(_directory, "recording.mp3");
            await File.WriteAllTextAsync(file, string.Empty);
            await File.WriteAllTextAsync(Path.ChangeExtension(file, ".srt"), "Existing transcript");

            TranscriptionInput? input = await ResolveAsync(new AppOptions { InputFilePath = file });

            Assert.That(input, Is.Not.Null);
            Assert.That(input!.Files.Select(media => media.FullName), Is.EqualTo(new[] { file }));
            Assert.That(input.FolderMode, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CompletedFolderDoesNotSelectMicrophoneMode(bool namedFolder)
        {
            string nested = Directory.CreateDirectory(Path.Combine(_directory, "nested")).FullName;
            string file = Path.Combine(nested, "complete.mp3");
            await File.WriteAllTextAsync(file, string.Empty);
            await File.WriteAllTextAsync(Path.ChangeExtension(file, ".srt"), "Existing transcript");
            AppOptions options = new() { Recursive = true };
            if (namedFolder)
            {
                options.FolderPath = _directory;
            }
            else
            {
                options.InputFilePath = _directory;
            }

            Assert.That(await ResolveAsync(options), Is.Null);
        }

        [Test]
        public async Task ConflictingInputsDoNotSelectAFileOrMicrophone()
        {
            AppOptions options = new() { FolderPath = _directory, InputFilePath = "recording.mp3" };

            Assert.That(await ResolveAsync(options), Is.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task MissingInputDoesNotSelectMicrophoneMode(bool namedFolder)
        {
            string missing = Path.Combine(_directory, "missing");
            AppOptions options = new();
            if (namedFolder)
            {
                options.FolderPath = missing;
            }
            else
            {
                options.InputFilePath = missing;
            }

            Assert.That(await ResolveAsync(options), Is.Null);
        }

        private Task<TranscriptionInput?> ResolveAsync(AppOptions options)
        {
            return MediaInputResolver.ResolveAsync(options, _logger, CancellationToken.None);
        }
    }
}
