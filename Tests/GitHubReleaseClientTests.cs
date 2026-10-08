using System.Net;
using System.Security.Cryptography;
using NUnit.Framework;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    [TestFixture]
    public class GitHubReleaseClientTests
    {
        private string _directory = string.Empty;
        private string _downloadPath = string.Empty;
        private static readonly byte[] Payload = [1, 2, 3, 4];
        private static readonly string Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Payload));

        [SetUp]
        public void SetUp()
        {
            _directory = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "updater-tests", Guid.NewGuid().ToString("N"))).FullName;
            _downloadPath = Path.Combine(_directory, "download.exe");
        }

        [TearDown]
        public void TearDown()
        {
            File.Delete(_downloadPath);
            Directory.Delete(_directory);
        }

        [Test]
        public async Task PublishedAssetIsDownloadedAndVerified()
        {
            using ReleaseHttpHandler handler = new(ReleaseTestData.Metadata("1.2.3", Payload.Length, Digest), Payload);
            using HttpClient http = new(handler);
            GitHubReleaseClient client = new(http);

            GitHubRelease release = await client.GetLatestAsync(CancellationToken.None);
            await client.DownloadAsync(release, _downloadPath, CancellationToken.None);

            Assert.That(release.Version, Is.EqualTo(new Version(1, 2, 3)));
            Assert.That(await File.ReadAllBytesAsync(_downloadPath), Is.EqualTo(Payload));
            Assert.That(handler.RequestCount, Is.EqualTo(2));
        }

        [TestCase(null)]
        [TestCase("sha512:abcd")]
        [TestCase("sha256:1234")]
        public void MissingOrUnsupportedChecksumIsRejected(string? digest)
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata("1.2.3", 4, digest), Payload));
            GitHubReleaseClient client = new(http);

            Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
            Assert.That(File.Exists(_downloadPath), Is.False);
        }

        [TestCase("https://example.com/WhisperCLI.exe")]
        [TestCase("https://github.com/another/repository/releases/download/v1.2.3/WhisperCLI-win-x64.exe")]
        [TestCase("http://github.com/bvdcode/WhisperCLI/releases/download/v1.2.3/WhisperCLI-win-x64.exe")]
        public void AssetOutsideTheExpectedReleaseIsRejected(string url)
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata("1.2.3", 4, Digest, url), Payload));
            GitHubReleaseClient client = new(http);

            Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void UnpublishedOrPrereleaseVersionsAreRejected(bool draft, bool prerelease)
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata("1.2.3", 4, Digest, draft: draft, prerelease: prerelease), Payload));
            GitHubReleaseClient client = new(http);

            Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
        }

        [Test]
        public void MissingWindowsExecutableIsRejected()
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata("1.2.3", 4, Digest, assetName: "other.exe"), Payload));
            GitHubReleaseClient client = new(http);

            Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
        }

        [Test]
        public void ReleaseLookupFailureIsReported()
        {
            using HttpClient http = new(new ReleaseHttpHandler("{}", [], HttpStatusCode.Forbidden));
            GitHubReleaseClient client = new(http);

            Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestAsync(CancellationToken.None));
        }

        [Test]
        public async Task CorruptPayloadIsRejected()
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata("1.2.3", 4, "sha256:" + new string('0', 64)), Payload));
            GitHubReleaseClient client = new(http);
            GitHubRelease release = await client.GetLatestAsync(CancellationToken.None);

            Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, _downloadPath, CancellationToken.None));
        }

        [Test]
        public async Task TruncatedPayloadIsRejected()
        {
            using HttpClient http = new(new ReleaseHttpHandler(ReleaseTestData.Metadata("1.2.3", 5, Digest), Payload));
            GitHubReleaseClient client = new(http);
            GitHubRelease release = await client.GetLatestAsync(CancellationToken.None);

            Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, _downloadPath, CancellationToken.None));
        }

        [Test]
        public async Task ExistingFileIsNeverOverwrittenDuringDownload()
        {
            await File.WriteAllBytesAsync(_downloadPath, Payload);
            using ReleaseHttpHandler handler = new("{}", []);
            using HttpClient http = new(handler);
            GitHubReleaseClient client = new(http);
            GitHubRelease release = new(new Version(1, 2, 3), new Uri("https://github.com/example.exe"), 4, SHA256.HashData(Payload));

            Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(release, _downloadPath, CancellationToken.None));
            Assert.That(await File.ReadAllBytesAsync(_downloadPath), Is.EqualTo(Payload));
            Assert.That(handler.RequestCount, Is.Zero);
        }
    }
}
