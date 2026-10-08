using System.Diagnostics;
using Serilog;

namespace WhisperCLI.Updates
{
    internal class SelfUpdater(GitHubReleaseClient releases, UpdateInstaller installer, ILogger logger)
    {
        private const string ProductName = "WhisperCLI";

        public async Task UpdateAsync(string executablePath, CancellationToken token)
        {
            Version currentVersion = GetVersion(executablePath);
            logger.Information("Checking the latest GitHub Release. Current version: {version}", currentVersion);
            GitHubRelease release = await releases.GetLatestAsync(token).ConfigureAwait(false);
            if (release.Version <= currentVersion)
            {
                logger.Information("WhisperCLI {version} is already up to date.", currentVersion);
                return;
            }

            string directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("The executable directory is not available.");
            string stagedPath = Path.Combine(directory, $".{Path.GetFileName(executablePath)}.{Guid.NewGuid():N}.update.exe");
            bool scheduled = false;
            try
            {
                logger.Information("Downloading WhisperCLI {version} ({size:0.0} MB)...", release.Version, release.Size / 1048576.0);
                await releases.DownloadAsync(release, stagedPath, token).ConfigureAwait(false);
                FileVersionInfo metadata = FileVersionInfo.GetVersionInfo(stagedPath);
                if (metadata.ProductName != ProductName || GetVersion(stagedPath) != release.Version)
                {
                    throw new InvalidDataException("The downloaded executable does not match the release product and version.");
                }

                await installer.ScheduleAsync(executablePath, stagedPath, release, token).ConfigureAwait(false);
                scheduled = true;
                logger.Information("Download verified. Version {version} will replace this executable after exit. Update log: {path}", release.Version, executablePath + ".update.log");
            }
            finally
            {
                if (!scheduled)
                {
                    File.Delete(stagedPath);
                }
            }
        }

        private static Version GetVersion(string path)
        {
            string? productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (productVersion is null || !Version.TryParse(productVersion.Split('+', '-')[0], out Version? version) || version.Build < 0)
            {
                throw new InvalidDataException("The executable does not contain a valid product version.");
            }

            return new Version(version.Major, version.Minor, version.Build);
        }
    }
}
