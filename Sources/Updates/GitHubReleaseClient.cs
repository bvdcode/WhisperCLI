using System.Security.Cryptography;
using System.Text.Json;

namespace WhisperCLI.Updates
{
    internal class GitHubReleaseClient(HttpClient httpClient)
    {
        public const string AssetName = "WhisperCLI-win-x64.exe";
        private const string Repository = "bvdcode/WhisperCLI";
        private const string Sha256Prefix = "sha256:";
        private static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");

        public async Task<GitHubRelease> GetLatestAsync(CancellationToken token)
        {
            using HttpResponseMessage response = await httpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            JsonElement release = document.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            {
                throw new InvalidDataException("The latest GitHub Release is not a stable published release.");
            }

            string? tag = release.GetProperty("tag_name").GetString();
            if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out Version? version) || version.Build < 0 || version.Revision >= 0)
            {
                throw new InvalidDataException("The release tag is not a supported stable version.");
            }

            JsonElement asset = release.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(candidate => candidate.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind == JsonValueKind.Undefined || asset.GetProperty("state").GetString() != "uploaded")
            {
                throw new InvalidDataException($"The latest release does not contain {AssetName}.");
            }

            string expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
            if (asset.GetProperty("browser_download_url").GetString() != expectedUrl)
            {
                throw new InvalidDataException("The executable download URL does not belong to this release.");
            }

            string? digest = asset.GetProperty("digest").GetString();
            if (digest is null || !digest.StartsWith(Sha256Prefix, StringComparison.Ordinal) || digest.Length != Sha256Prefix.Length + 64)
            {
                throw new InvalidDataException("GitHub did not provide a SHA-256 checksum for the release executable.");
            }

            byte[] sha256 = Convert.FromHexString(digest[Sha256Prefix.Length..]);
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0)
            {
                throw new InvalidDataException("The release executable has an invalid size.");
            }

            return new GitHubRelease(version, new Uri(expectedUrl), size, sha256);
        }

        public async Task DownloadAsync(GitHubRelease release, string destination, CancellationToken token)
        {
            await using FileStream file = new(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
            using HttpResponseMessage response = await httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await stream.CopyToAsync(file, token).ConfigureAwait(false);
            await file.FlushAsync(token).ConfigureAwait(false);
            if (file.Length != release.Size)
            {
                throw new InvalidDataException("The downloaded executable size does not match the release asset.");
            }

            file.Position = 0;
            byte[] sha256 = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(sha256, release.Sha256))
            {
                throw new InvalidDataException("SHA-256 verification failed. The current executable has not been changed.");
            }
        }
    }
}
