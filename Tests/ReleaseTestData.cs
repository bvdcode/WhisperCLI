using System.Text.Json;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    internal static class ReleaseTestData
    {
        public static string Metadata(string version, long size, string? digest,
            string? url = null, bool draft = false, bool prerelease = false, string state = "uploaded", string? assetName = null)
        {
            return JsonSerializer.Serialize(new
            {
                tag_name = "v" + version,
                draft,
                prerelease,
                assets = new[]
                {
                    new
                    {
                        name = assetName ?? GitHubReleaseClient.AssetName,
                        state,
                        size,
                        digest,
                        browser_download_url = url ?? $"https://github.com/bvdcode/WhisperCLI/releases/download/v{version}/{GitHubReleaseClient.AssetName}"
                    }
                }
            });
        }
    }
}
