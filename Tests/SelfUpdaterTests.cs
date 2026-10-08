using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class SelfUpdaterTests
    {
        private string _directory = string.Empty;
        private string _executable = string.Empty;
        private byte[] _original = [];
        private Version _version = new(0, 0, 0);

        [SetUp]
        public async Task SetUp()
        {
            _directory = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "updater-tests", Guid.NewGuid().ToString("N"))).FullName;
            _executable = Path.Combine(_directory, "WhisperCLI.exe");
            string assemblyPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "WhisperCLI.dll");
            _original = await File.ReadAllBytesAsync(assemblyPath);
            await File.WriteAllBytesAsync(_executable, _original);
            FileVersionInfo metadata = FileVersionInfo.GetVersionInfo(_executable);
            _version = new Version(metadata.ProductMajorPart, metadata.ProductMinorPart, metadata.ProductBuildPart);
        }

        [TearDown]
        public void TearDown()
        {
            File.Delete(_executable);
            Directory.Delete(_directory);
        }

        [Test]
        public async Task CurrentVersionDoesNotDownloadOrCreateHelperFiles()
        {
            using ReleaseHttpHandler handler = new(ReleaseTestData.Metadata(_version.ToString(), 1, "sha256:" + new string('0', 64)), []);
            using HttpClient http = new(handler);
            using Logger logger = new LoggerConfiguration().CreateLogger();
            SelfUpdater updater = new(new GitHubReleaseClient(http), new UpdateInstaller(logger), logger);

            await updater.UpdateAsync(_executable, CancellationToken.None);

            Assert.That(handler.RequestCount, Is.EqualTo(1));
            Assert.That(await File.ReadAllBytesAsync(_executable), Is.EqualTo(_original));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task ChecksumFailurePreservesInstalledFileAndCleansDownload()
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata(NextVersion(), 4, "sha256:" + new string('0', 64)), [1, 2, 3, 4]));
            using Logger logger = new LoggerConfiguration().CreateLogger();
            SelfUpdater updater = new(new GitHubReleaseClient(http), new UpdateInstaller(logger), logger);

            Assert.ThrowsAsync<InvalidDataException>(() => updater.UpdateAsync(_executable, CancellationToken.None));

            Assert.That(await File.ReadAllBytesAsync(_executable), Is.EqualTo(_original));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task WrongExecutableMetadataPreservesInstalledFileAndCleansDownload()
        {
            string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(_original));
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata(NextVersion(), _original.Length, digest), _original));
            using Logger logger = new LoggerConfiguration().CreateLogger();
            SelfUpdater updater = new(new GitHubReleaseClient(http), new UpdateInstaller(logger), logger);

            Assert.ThrowsAsync<InvalidDataException>(() => updater.UpdateAsync(_executable, CancellationToken.None));

            Assert.That(await File.ReadAllBytesAsync(_executable), Is.EqualTo(_original));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        }

        private string NextVersion()
        {
            return new Version(_version.Major + 1, 0, 0).ToString();
        }

        [Test]
        public async Task UnwritableInstallationFolderPreservesTheExecutable()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Ignore("Windows folder permissions are required for this test.");
                return;
            }

            using ReleaseHttpHandler handler = new(ReleaseTestData.Metadata(NextVersion(), 4, "sha256:" + new string('0', 64)), []);
            using HttpClient http = new(handler);
            using Logger logger = new LoggerConfiguration().CreateLogger();
            SelfUpdater updater = new(new GitHubReleaseClient(http), new UpdateInstaller(logger), logger);
            DirectoryInfo directory = new(_directory);
            DirectorySecurity originalPermissions = directory.GetAccessControl();
            DirectorySecurity restrictedPermissions = directory.GetAccessControl();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
            restrictedPermissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.CreateFiles, AccessControlType.Deny));
            directory.SetAccessControl(restrictedPermissions);
            try
            {
                Assert.ThrowsAsync<UnauthorizedAccessException>(() => updater.UpdateAsync(_executable, CancellationToken.None));
            }
            finally
            {
                directory.SetAccessControl(originalPermissions);
            }

            Assert.That(handler.RequestCount, Is.EqualTo(1));
            Assert.That(await File.ReadAllBytesAsync(_executable), Is.EqualTo(_original));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        }
    }
}
