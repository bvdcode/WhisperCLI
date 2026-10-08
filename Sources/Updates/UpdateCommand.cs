using Serilog;

namespace WhisperCLI.Updates
{
    internal static class UpdateCommand
    {
        public static async Task<int> RunAsync(AppOptions options, ILogger logger, CancellationToken token)
        {
            if (!OperatingSystem.IsWindows())
            {
                logger.Error("Self-update is supported only for the Windows executable.");
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(options.InputFilePath) || !string.IsNullOrWhiteSpace(options.FolderPath))
            {
                logger.Error("Use --update without an input file or --folder.");
                return 1;
            }

            string? executablePath = Environment.ProcessPath;
            if (executablePath is null || !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                logger.Error("Run --update from the WhisperCLI executable, rather than dotnet.");
                return 1;
            }

            using HttpClient httpClient = new();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperCLI-Updater");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                SelfUpdater updater = new(new GitHubReleaseClient(httpClient), new UpdateInstaller(logger), logger);
                await updater.UpdateAsync(executablePath, timeout.Token).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                logger.Information("Update canceled.");
                return 130;
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Error(ex, "Cannot write to the installation folder. Run --update with write access to that folder.");
                return 1;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Update failed.");
                return 1;
            }
        }
    }
}
