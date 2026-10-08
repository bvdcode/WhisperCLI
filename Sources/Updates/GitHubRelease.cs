namespace WhisperCLI.Updates
{
    internal record GitHubRelease(Version Version, Uri DownloadUrl, long Size, byte[] Sha256);
}
