using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    [TestFixture]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public class LinuxSelfUpdaterTests
    {
        private const UnixFileMode Permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        private string _directory = string.Empty;
        private string _executable = string.Empty;
        private byte[] _original = [];

        [SetUp]
        public async Task SetUp()
        {
            _directory = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "updater-tests", Guid.NewGuid() + " test's folder")).FullName;
            _executable = Path.Combine(_directory, "custom whisper");
            _original = Executable("WhisperCLI 1.2.2");
            await File.WriteAllBytesAsync(_executable, _original);
            File.SetUnixFileMode(_executable, Permissions);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task UpdateReplacesRunningRenamedExecutableAndPreservesPermissions()
        {
            ProcessStartInfo startInfo = new(_executable) { RedirectStandardInput = true, RedirectStandardOutput = true };
            using Process running = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the installed executable.");
            try
            {
                Assert.That(await running.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("ready"));
                byte[] replacement = Executable("WhisperCLI 1.2.3");
                using ReleaseHttpHandler handler = Handler("1.2.3", replacement);

                await UpdateAsync(handler);

                Assert.That(running.HasExited, Is.False);
                Assert.That(await File.ReadAllBytesAsync(_executable), Is.EqualTo(replacement));
                Assert.That(File.GetUnixFileMode(_executable), Is.EqualTo(Permissions));
                Assert.That(await ExecutableVersion.ReadAsync(_executable, CancellationToken.None), Is.EqualTo(new Version(1, 2, 3)));
                Assert.That(handler.RequestCount, Is.EqualTo(2));
                Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
                await running.StandardInput.WriteLineAsync("exit");
                await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                if (!running.HasExited)
                {
                    running.Kill();
                    await running.WaitForExitAsync();
                }
            }
        }

        [Test]
        public async Task CurrentVersionDoesNotDownload()
        {
            using ReleaseHttpHandler handler = Handler("1.2.2", Executable("WhisperCLI 1.2.2"));

            await UpdateAsync(handler);

            Assert.That(handler.RequestCount, Is.EqualTo(1));
            await AssertUnchangedAsync();
        }

        [Test]
        public async Task BadChecksumPreservesTheInstalledExecutable()
        {
            byte[] replacement = Executable("WhisperCLI 1.2.3");
            using ReleaseHttpHandler handler = new(ReleaseTestData.MultiplatformMetadata("1.2.3", replacement.Length, "sha256:" + new string('0', 64)), replacement);

            Assert.ThrowsAsync<InvalidDataException>(() => UpdateAsync(handler));

            await AssertUnchangedAsync();
        }

        [TestCase("AnotherProduct 1.2.3")]
        [TestCase("WhisperCLI 1.2.2")]
        [TestCase("WhisperCLI invalid")]
        public async Task IncorrectProductOrVersionPreservesTheInstalledExecutable(string heading)
        {
            using ReleaseHttpHandler handler = Handler("1.2.3", Executable(heading));

            Assert.ThrowsAsync<InvalidDataException>(() => UpdateAsync(handler));

            await AssertUnchangedAsync();
        }

        [Test]
        public async Task MissingLinuxAssetDoesNotDownloadWindowsExecutable()
        {
            using ReleaseHttpHandler handler = new(ReleaseTestData.Metadata("1.2.3", 1, "sha256:" + new string('0', 64)), []);

            Assert.ThrowsAsync<InvalidDataException>(() => UpdateAsync(handler));

            Assert.That(handler.RequestCount, Is.EqualTo(1));
            await AssertUnchangedAsync();
        }

        private async Task UpdateAsync(ReleaseHttpHandler handler)
        {
            using HttpClient http = new(handler);
            using Logger logger = new LoggerConfiguration().CreateLogger();
            SelfUpdater updater = new(new GitHubReleaseClient(http, ReleasePlatform.LinuxX64), new UpdateInstaller(logger), logger);
            await updater.UpdateAsync(_executable, CancellationToken.None);
        }

        private async Task AssertUnchangedAsync()
        {
            Assert.That(await File.ReadAllBytesAsync(_executable), Is.EqualTo(_original));
            Assert.That(File.GetUnixFileMode(_executable), Is.EqualTo(Permissions));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        }

        private static ReleaseHttpHandler Handler(string version, byte[] payload)
        {
            return new ReleaseHttpHandler(ReleaseTestData.MultiplatformMetadata(version, payload.Length, "sha256:" + Convert.ToHexString(SHA256.HashData(payload))), payload);
        }

        private static byte[] Executable(string heading)
        {
            return Encoding.UTF8.GetBytes($"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then\n  printf '%s\\n' '{heading}'\nelse\n  printf 'ready\\n'\n  read -r input\nfi\n");
        }
    }
}
