using Serilog;

namespace WhisperCLI.Updates
{
    internal class SelfUpdater(GitHubReleaseClient releases, UpdateInstaller installer, ILogger logger)
    {
        public async Task UpdateAsync(string executablePath, CancellationToken token)
        {
            Version currentVersion = await ExecutableVersion.ReadAsync(executablePath, token).ConfigureAwait(false);
            logger.Information("Checking the latest GitHub Release. Current version: {version}", currentVersion);
            GitHubRelease release = await releases.GetLatestAsync(token).ConfigureAwait(false);
            if (release.Version <= currentVersion)
            {
                logger.Information("WhisperCLI {version} is already up to date.", currentVersion);
                return;
            }

            string directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("The executable directory is not available.");
            string stagedPath = Path.Combine(directory, $".{Path.GetFileName(executablePath)}.{Guid.NewGuid():N}.update{Path.GetExtension(executablePath)}");
            bool installationStarted = false;
            try
            {
                logger.Information("Downloading WhisperCLI {version} ({size:0.0} MB)...", release.Version, release.Size / 1048576.0);
                await releases.DownloadAsync(release, stagedPath, token).ConfigureAwait(false);
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(stagedPath, File.GetUnixFileMode(executablePath));
                }
                if (await ExecutableVersion.ReadAsync(stagedPath, token).ConfigureAwait(false) != release.Version)
                {
                    throw new InvalidDataException("The downloaded executable does not match the release product and version.");
                }

                await installer.InstallAsync(executablePath, stagedPath, release, token).ConfigureAwait(false);
                installationStarted = true;
                if (OperatingSystem.IsWindows())
                {
                    logger.Information("Download verified. Version {version} will replace this executable after exit. Update log: {path}", release.Version, executablePath + ".update.log");
                }
                else
                {
                    logger.Information("Updated successfully to {version}.", release.Version);
                }
            }
            finally
            {
                if (!installationStarted)
                {
                    File.Delete(stagedPath);
                }
            }
        }
    }
}
