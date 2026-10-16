using System.Runtime.InteropServices;

namespace WhisperCLI.Updates
{
    internal static class ReleaseAssets
    {
        public static ReleasePlatform CurrentPlatform => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, false, Architecture.X64) => ReleasePlatform.WindowsX64,
            (false, true, Architecture.X64) => ReleasePlatform.LinuxX64,
            _ => throw new PlatformNotSupportedException("Self-update requires a Windows x64 or Linux x64 executable.")
        };

        public static string GetName(ReleasePlatform platform)
        {
            return platform switch
            {
                ReleasePlatform.WindowsX64 => "WhisperCLI.exe",
                ReleasePlatform.LinuxX64 => "WhisperCLI-linux-x64",
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported release platform.")
            };
        }
    }
}
