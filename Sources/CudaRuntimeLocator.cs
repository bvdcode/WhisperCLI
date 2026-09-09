using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WhisperCLI;

internal sealed class CudaRuntimeProbe
{
    public bool Ready { get; init; }
    public string NativeCudaLibraryPath { get; init; } = string.Empty;
    public string NativeCudaDirectory { get; init; } = string.Empty;
    public int? RequiredCudaMajor { get; init; }
    public List<string> LibraryDirectories { get; init; } = [];
    public List<string> MissingDependencies { get; init; } = [];
    public List<string> FoundCudaLibraries { get; init; } = [];
    public string LddOutput { get; init; } = string.Empty;
    public string? RuntimeMismatch { get; init; }
}

internal static class CudaRuntimeLocator
{
    // Whisper.net.Runtime.Cuda.Linux 1.8.1 was the CUDA-12 generation used by the
    // original project. v13 pins that package exactly. The runtime also repairs a missing
    // output copy from the exact NuGet package cache before diagnosing host CUDA libraries.
    private const int ExpectedPinnedCudaMajor = 12;

    private static readonly object Sync = new();
    private static CudaRuntimeProbe? _cached;

    public static CudaRuntimeProbe Probe()
    {
        lock (Sync)
        {
            return _cached ??= ProbeCore();
        }
    }

    public static void LogAndValidate(ILogger logger)
    {
        CudaRuntimeProbe probe = Probe();

        if (!string.IsNullOrWhiteSpace(probe.RuntimeMismatch))
        {
            throw new WhisperRuntimeUnavailableException(
                probe.RuntimeMismatch + "\n" +
                "This build pins Whisper.net + Whisper.net.Runtime + Whisper.net.Runtime.Cuda.Linux to exactly 1.8.1 and removes stale Whisper native files before copying build output. " +
                "Run `rm -rf bin obj && dotnet restore --force` once after applying v13. If this message remains after that clean rebuild, show `dotnet list package --include-transitive | grep Whisper` and the ldd output below.\n" +
                $"ldd against {probe.NativeCudaLibraryPath}:\n{Tail(probe.LddOutput, 8000)}");
        }

        if (probe.Ready)
        {
            logger.Information(
                "CUDA dependency preflight succeeded. Native CUDA runtime requires CUDA major {major}; library search dirs={dirs}",
                probe.RequiredCudaMajor?.ToString() ?? "unknown",
                probe.LibraryDirectories.Count == 0 ? "<system loader paths>" : string.Join(Path.PathSeparator, probe.LibraryDirectories));
            return;
        }

        string missing = probe.MissingDependencies.Count == 0
            ? "unknown native CUDA dependency"
            : string.Join(", ", probe.MissingDependencies);
        string found = probe.FoundCudaLibraries.Count == 0
            ? "<none>"
            : string.Join(", ", probe.FoundCudaLibraries);
        string majorText = probe.RequiredCudaMajor is int major ? $"CUDA {major}" : "the CUDA generation required by the native binary";

        throw new WhisperRuntimeUnavailableException(
            "Linux/NVIDIA transcription requires CUDA because the Vulkan backend has repeatedly segfaulted on this machine. " +
            $"The pinned Whisper native binary requires {majorText}, but CUDA preflight failed. Missing: {missing}. Found CUDA libraries: {found}. " +
            "The CLI searched ldconfig, LD_LIBRARY_PATH, CUDA_HOME/CUDA_PATH, /usr/local/cuda*, common system locations, Conda, and Python nvidia-* package directories. " +
            "If the required CUDA runtime is already installed, expose its lib64/targets/x86_64-linux/lib directory; otherwise install that CUDA runtime/toolkit.\n" +
            $"ldd against {probe.NativeCudaLibraryPath}:\n{Tail(probe.LddOutput, 8000)}");
    }

    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        CudaRuntimeProbe probe = Probe();
        if (!probe.Ready)
        {
            throw new WhisperRuntimeUnavailableException(
                "CUDA worker launch was requested before CUDA dependency preflight succeeded.");
        }

        string existing = startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out string? value)
            ? value ?? string.Empty
            : Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;

        IEnumerable<string> parts = probe.LibraryDirectories;
        if (!string.IsNullOrWhiteSpace(existing))
        {
            parts = parts.Concat(existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        string merged = string.Join(
            Path.PathSeparator,
            parts.Where(Directory.Exists).Distinct(StringComparer.Ordinal));

        if (!string.IsNullOrWhiteSpace(merged))
        {
            startInfo.Environment["LD_LIBRARY_PATH"] = merged;
        }

        startInfo.Environment["CUDA_VISIBLE_DEVICES"] = "0";
    }

    private static CudaRuntimeProbe ProbeCore()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new CudaRuntimeProbe { Ready = true };
        }

        string nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "linux-x64");
        string nativeCuda = Path.Combine(nativeDir, "libggml-cuda-whisper.so");

        // NuGet runtime packages add native files through .targets. A previous v12 cleanup
        // target could delete those files after NuGet had already copied them. v13 removes
        // that target and also repairs development output from the exact NuGet cache so the
        // diagnostic cannot confuse "file was deleted from bin" with "CUDA is not installed".
        if (!File.Exists(nativeCuda))
        {
            TryRepairNativeRuntimeFromNuGetCache(nativeDir, nativeCuda);
        }

        HashSet<string> directories = new(StringComparer.Ordinal);
        // First: the runtime's own directory. Its libggml/libwhisper dependencies are bundled
        // next to libggml-cuda-whisper.so and must participate in ldd resolution.
        AddDirectory(directories, nativeDir);
        AddEnvironmentPath(directories, Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"));
        AddCudaRoot(directories, Environment.GetEnvironmentVariable("CUDA_HOME"));
        AddCudaRoot(directories, Environment.GetEnvironmentVariable("CUDA_PATH"));
        AddCudaRoot(directories, Environment.GetEnvironmentVariable("CUDA_ROOT"));

        string? conda = Environment.GetEnvironmentVariable("CONDA_PREFIX");
        if (!string.IsNullOrWhiteSpace(conda))
        {
            AddDirectory(directories, Path.Combine(conda, "lib"));
            AddPythonNvidiaPackageDirs(directories, Path.Combine(conda, "lib"));
        }

        AddDirectory(directories, "/usr/lib/x86_64-linux-gnu");
        AddDirectory(directories, "/lib/x86_64-linux-gnu");
        AddDirectory(directories, "/usr/lib64");
        AddDirectory(directories, "/usr/local/lib64");
        AddDirectory(directories, "/usr/local/lib");
        AddDirectory(directories, "/usr/lib/wsl/lib");
        AddCudaRoot(directories, "/usr/local/cuda");
        AddCudaRoot(directories, "/opt/cuda");
        AddWildcardCudaRoots(directories, "/usr/local", "cuda-*");
        AddWildcardCudaRoots(directories, "/opt", "cuda-*");

        AddLdConfigDirectories(directories);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddPythonNvidiaPackageDirs(directories, Path.Combine(home, ".local", "lib"));
            AddPythonNvidiaPackageDirs(directories, Path.Combine(home, ".local", "share", "virtualenvs"));
        }
        AddPythonNvidiaPackageDirs(directories, "/usr/local/lib");
        AddPythonNvidiaPackageDirs(directories, "/usr/lib");
        AddPythonNvidiaPackageDirs(directories, "/opt/conda/lib");

        List<string> orderedDirs = directories
            .Where(Directory.Exists)
            .OrderByDescending(path => string.Equals(path, nativeDir, StringComparison.Ordinal))
            .ThenByDescending(ContainsCudaRuntime)
            .ThenByDescending(ContainsCublas)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

        List<string> found = FindInterestingLibraries(orderedDirs);
        List<string> missing = [];
        if (!File.Exists(nativeCuda))
        {
            missing.Add("Whisper.net CUDA native runtime (libggml-cuda-whisper.so) was not copied to output and could not be repaired from the NuGet 1.8.1 cache");
        }

        string ldd = File.Exists(nativeCuda) ? RunLdd(nativeCuda, orderedDirs) : string.Empty;
        int? requiredMajor = ExtractCudaMajor(ldd);
        string? mismatch = null;
        if (requiredMajor is int actualMajor && actualMajor != ExpectedPinnedCudaMajor)
        {
            mismatch =
                $"Native runtime version mismatch: this v13 source pins Whisper.net.Runtime.Cuda.Linux 1.8.1 (CUDA {ExpectedPinnedCudaMajor}), " +
                $"but the built libggml-cuda-whisper.so requires CUDA {actualMajor}. This is strong evidence of stale/mixed native files in bin/obj from the earlier 1.9.1 build.";
        }

        foreach (Match match in Regex.Matches(ldd, @"^\s*([^\s]+)\s*=>\s*not found\s*$", RegexOptions.Multiline))
        {
            string library = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(library) || IsBundledWhisperDependency(library))
            {
                continue;
            }
            if (!missing.Contains(library, StringComparer.Ordinal))
            {
                missing.Add(library);
            }
        }

        // ldd is authoritative for the exact binary. Do not hard-code libcudart.so.12 or
        // libcublas.so.12 here; if package/runtime generation ever changes, the diagnostic
        // must report what that ELF actually asks for.
        return new CudaRuntimeProbe
        {
            Ready = mismatch is null && missing.Count == 0,
            NativeCudaLibraryPath = nativeCuda,
            NativeCudaDirectory = nativeDir,
            RequiredCudaMajor = requiredMajor,
            LibraryDirectories = orderedDirs,
            MissingDependencies = missing,
            FoundCudaLibraries = found,
            LddOutput = ldd,
            RuntimeMismatch = mismatch
        };
    }

    private static void TryRepairNativeRuntimeFromNuGetCache(string destinationDir, string expectedCudaLibrary)
    {
        try
        {
            foreach (string packageRoot in GetNuGetPackageRoots())
            {
                string exactPackage = Path.Combine(
                    packageRoot,
                    "whisper.net.runtime.cuda.linux",
                    "1.8.1");

                if (!Directory.Exists(exactPackage))
                {
                    continue;
                }

                string? cudaLibrary = Directory
                    .EnumerateFiles(exactPackage, "libggml-cuda-whisper.so", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (cudaLibrary is null)
                {
                    continue;
                }

                string sourceDir = Path.GetDirectoryName(cudaLibrary)!;
                Directory.CreateDirectory(destinationDir);

                foreach (string source in Directory.EnumerateFiles(sourceDir, "*.so*", SearchOption.TopDirectoryOnly))
                {
                    string destination = Path.Combine(destinationDir, Path.GetFileName(source));
                    File.Copy(source, destination, overwrite: true);
                }

                if (File.Exists(expectedCudaLibrary))
                {
                    return;
                }
            }
        }
        catch
        {
            // The caller reports the missing native runtime with ldd/package diagnostics.
            // Repair is best-effort because published/install directories can be read-only.
        }
    }

    private static IEnumerable<string> GetNuGetPackageRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".nuget", "packages");
        }
    }

    private static int? ExtractCudaMajor(string ldd)
    {
        Match match = Regex.Match(ldd, @"lib(?:cudart|cublas)(?:Lt)?\.so\.(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int major) ? major : null;
    }

    private static bool IsBundledWhisperDependency(string library) =>
        library.StartsWith("libggml-", StringComparison.Ordinal) ||
        library.StartsWith("libwhisper", StringComparison.Ordinal);

    private static string RunLdd(string nativeLibrary, IReadOnlyList<string> dirs)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = File.Exists("/usr/bin/ldd") ? "/usr/bin/ldd" : "ldd",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(nativeLibrary);
            string current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
            string merged = string.Join(
                Path.PathSeparator,
                dirs.Concat(current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.Ordinal));
            if (!string.IsNullOrWhiteSpace(merged))
            {
                psi.Environment["LD_LIBRARY_PATH"] = merged;
            }

            using Process process = new() { StartInfo = psi };
            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return stdout + "\n" + stderr + "\n[ldd timed out]";
            }
            return stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "\n" + stderr);
        }
        catch (Exception ex)
        {
            return $"[ldd failed: {ex.Message}]";
        }
    }

    private static void AddEnvironmentPath(HashSet<string> dirs, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (string part in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddDirectory(dirs, part);
        }
    }

    private static void AddCudaRoot(HashSet<string> dirs, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        AddDirectory(dirs, Path.Combine(root, "lib64"));
        AddDirectory(dirs, Path.Combine(root, "lib"));
        AddDirectory(dirs, Path.Combine(root, "targets", "x86_64-linux", "lib"));
    }

    private static void AddWildcardCudaRoots(HashSet<string> dirs, string parent, string pattern)
    {
        if (!Directory.Exists(parent)) return;
        try
        {
            foreach (string root in Directory.EnumerateDirectories(parent, pattern, SearchOption.TopDirectoryOnly))
            {
                AddCudaRoot(dirs, root);
            }
        }
        catch { }
    }

    private static void AddLdConfigDirectories(HashSet<string> dirs)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = File.Exists("/sbin/ldconfig") ? "/sbin/ldconfig" : "ldconfig",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-p");
            using Process process = new() { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2500);
            foreach (string line in output.Split('\n'))
            {
                if (!line.Contains("libcudart", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("libcublas", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("libnvJitLink", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int arrow = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrow < 0) continue;
                string path = line[(arrow + 2)..].Trim();
                AddDirectory(dirs, Path.GetDirectoryName(path));
            }
        }
        catch { }
    }

    private static void AddPythonNvidiaPackageDirs(HashSet<string> dirs, string pythonLibRoot)
    {
        if (!Directory.Exists(pythonLibRoot)) return;
        try
        {
            IEnumerable<string> pythonDirs = Directory.EnumerateDirectories(pythonLibRoot, "python*", SearchOption.TopDirectoryOnly);
            foreach (string pythonDir in pythonDirs.Take(20))
            {
                AddNvidiaSitePackages(dirs, Path.Combine(pythonDir, "site-packages", "nvidia"));
                AddNvidiaSitePackages(dirs, Path.Combine(pythonDir, "dist-packages", "nvidia"));
            }
        }
        catch { }
    }

    private static void AddNvidiaSitePackages(HashSet<string> dirs, string nvidiaRoot)
    {
        if (!Directory.Exists(nvidiaRoot)) return;
        foreach (string component in new[] { "cuda_runtime", "cublas", "nvjitlink", "cusparse", "cusolver" })
        {
            AddDirectory(dirs, Path.Combine(nvidiaRoot, component, "lib"));
        }
    }

    private static void AddDirectory(HashSet<string> dirs, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) dirs.Add(full);
        }
        catch { }
    }

    private static bool ContainsCudaRuntime(string dir) => ContainsAny([dir], "libcudart.so");
    private static bool ContainsCublas(string dir) => ContainsAny([dir], "libcublas.so");

    private static bool ContainsAny(IEnumerable<string> dirs, params string[] prefixes)
    {
        foreach (string dir in dirs)
        {
            foreach (string prefix in prefixes)
            {
                try
                {
                    if (Directory.EnumerateFiles(dir, prefix + "*", SearchOption.TopDirectoryOnly).Any()) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static List<string> FindInterestingLibraries(IEnumerable<string> dirs)
    {
        List<string> found = [];
        string[] patterns = ["libcudart.so*", "libcublas.so*", "libcublasLt.so*", "libnvJitLink.so*"];
        foreach (string dir in dirs)
        {
            foreach (string pattern in patterns)
            {
                try
                {
                    foreach (string path in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Take(3))
                    {
                        found.Add(path);
                    }
                }
                catch { }
            }
        }
        return found.Distinct(StringComparer.Ordinal).Take(30).ToList();
    }

    private static string Tail(string text, int maxChars) =>
        string.IsNullOrEmpty(text) || text.Length <= maxChars ? text : text[^maxChars..];
}
