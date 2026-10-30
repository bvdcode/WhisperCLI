using System.Text.Json;
using System.Text.Json.Nodes;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    internal static class ReleaseTestData
    {
        public static string MultiplatformMetadata(string version, long size, string digest)
        {
            JsonNode metadata = JsonNode.Parse(Metadata(version, size, digest))!;
            string linuxName = ReleaseAssets.GetName(ReleasePlatform.LinuxX64);
            JsonNode linux = JsonNode.Parse(Metadata(version, size, digest,
                url: $"https://github.com/bvdcode/WhisperCLI/releases/download/v{version}/{linuxName}", assetName: linuxName))!;
            metadata["assets"]!.AsArray().Add(linux["assets"]![0]!.DeepClone());
            return metadata.ToJsonString();
        }

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
                        name = assetName ?? ReleaseAssets.GetName(ReleasePlatform.WindowsX64),
                        state,
                        size,
                        digest,
                        browser_download_url = url ?? $"https://github.com/bvdcode/WhisperCLI/releases/download/v{version}/{ReleaseAssets.GetName(ReleasePlatform.WindowsX64)}"
                    }
                }
            });
        }
    }
}
