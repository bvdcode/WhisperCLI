using System.Diagnostics;

namespace WhisperCLI.Updates
{
    internal static class ExecutableVersion
    {
        private const string ProductName = "WhisperCLI";

        public static async Task<Version> ReadAsync(string path, CancellationToken token)
        {
            if (OperatingSystem.IsWindows())
            {
                FileVersionInfo metadata = FileVersionInfo.GetVersionInfo(path);
                if (metadata.ProductName != ProductName)
                {
                    throw new InvalidDataException("The executable does not belong to WhisperCLI.");
                }
                return Parse(metadata.ProductVersion);
            }

            ProcessStartInfo startInfo = new(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--version");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not read the executable version.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string versionText = await error.ConfigureAwait(false);
                await output.ConfigureAwait(false);
                string[] heading = versionText.Split('\n')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (process.ExitCode != 0 || heading.Length != 2 || heading[0] != ProductName)
                {
                    throw new InvalidDataException($"The executable did not report a valid WhisperCLI version: {versionText.Trim()}");
                }
                return Parse(heading[1]);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private static Version Parse(string? productVersion)
        {
            if (productVersion is null || !Version.TryParse(productVersion.Split('+', '-')[0], out Version? version) || version.Build < 0)
            {
                throw new InvalidDataException("The executable does not contain a valid product version.");
            }
            return new Version(version.Major, version.Minor, version.Build);
        }
    }
}
