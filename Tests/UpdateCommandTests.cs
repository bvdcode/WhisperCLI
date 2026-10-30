using CommandLine;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class UpdateCommandTests
    {
        [Test]
        public void UpdateFlagIsParsedWithoutAnInputFile()
        {
            ParserResult<AppOptions> parsed = Parser.Default.ParseArguments<AppOptions>(["--update"]);

            Assert.That(parsed, Is.InstanceOf<Parsed<AppOptions>>());
            Assert.That(((Parsed<AppOptions>)parsed).Value.Update, Is.True);
        }

        [TestCase("file.mp3", "")]
        [TestCase("", "folder")]
        public async Task UpdateRejectsSimultaneousTranscription(string file, string folder)
        {
            using Logger logger = new LoggerConfiguration().CreateLogger();
            AppOptions options = new() { Update = true, InputFilePath = file, FolderPath = folder };

            int exitCode = await UpdateCommand.RunAsync(options, logger, CancellationToken.None);

            Assert.That(exitCode, Is.EqualTo(1));
        }
    }
}
