using Serilog;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace WhisperCLI
{
    internal static class FFmpegBootstrapper
    {
        private static readonly SemaphoreSlim CheckLock = new(1, 1);
        private static string? configuredPath;

        public static async Task EnsureAvailableAsync(ILogger logger, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return;
            }

            await CheckLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    return;
                }

                logger.Information("Checking FFmpeg...");

                string appDataDirectory = AppPaths.FFmpegDirectory;
                if (TryConfigureFromDirectory(appDataDirectory, logger, "application data"))
                {
                    return;
                }

                if (TryConfigureFromPath(logger))
                {
                    return;
                }

                FFmpeg.SetExecutablesPath(appDataDirectory);
                logger.Information("FFmpeg not found in application data or PATH - downloading to {ffmpegPath}", appDataDirectory);
                await FFmpegDownloader
                    .GetLatestVersion(FFmpegVersion.Official, FFmpeg.ExecutablesPath, new FFMpegDownloadingProgress(logger))
                    .WaitAsync(TimeSpan.FromMinutes(10), token)
                    .ConfigureAwait(false);

                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    Exec("chmod +x " + Path.Combine(appDataDirectory, "ffmpeg"));
                    Exec("chmod +x " + Path.Combine(appDataDirectory, "ffprobe"));
                }

                configuredPath = appDataDirectory;
                logger.Information("FFmpeg downloaded to {ffmpegPath}", appDataDirectory);
            }
            finally
            {
                CheckLock.Release();
            }
        }

        private static bool TryConfigureFromDirectory(string directory, ILogger logger, string source)
        {
            if (!Directory.Exists(directory) || !ContainsRequiredExecutables(directory))
            {
                return false;
            }

            FFmpeg.SetExecutablesPath(directory);
            configuredPath = directory;
            logger.Information("Using FFmpeg from {source}: {ffmpegPath}", source, directory);
            return true;
        }

        private static bool TryConfigureFromPath(ILogger logger)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (string rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    directory = Path.GetFullPath(directory);
                }
                catch
                {
                    continue;
                }

                if (TryConfigureFromDirectory(directory, logger, "PATH"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsRequiredExecutables(string directory)
        {
            return HasExecutable(directory, "ffmpeg") && HasExecutable(directory, "ffprobe");
        }

        private static bool HasExecutable(string directory, string name)
        {
            foreach (string candidate in GetExecutableNames(name))
            {
                if (File.Exists(Path.Combine(directory, candidate)))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> GetExecutableNames(string name)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                yield return name + ".exe";
            }

            yield return name;
        }

        private static void Exec(string cmd)
        {
            var escapedArgs = cmd.Replace("\"", "\\\"");

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\""
                }
            };

            process.Start();
            process.WaitForExit();
        }
    }
}
