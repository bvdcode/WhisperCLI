using Serilog;
using System.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace WhisperCLI;

internal sealed class WhisperRuntimeUnavailableException : InvalidOperationException
{
    public WhisperRuntimeUnavailableException(string message) : base(message) { }
    public WhisperRuntimeUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class WhisperRuntimeManager
{
    private static readonly object Sync = new();
    private static bool _configured;
    private static bool _runtimeLogged;
    private static readonly Queue<string> NativeLogRing = new();
    private const int NativeLogRingCapacity = 120;

    public static bool NvidiaDetected { get; private set; }
    public static bool RequireGpu { get; private set; }
    public static bool UseGpu { get; private set; } = true;
    public static string RuntimePreference { get; private set; } = "auto";
    public static string? NvidiaDescription { get; private set; }
    public static string NvidiaDetectionMethod { get; private set; } = "none";

    public static void ObserveNativeLog(string level, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string line = $"[{level.ToUpperInvariant()}] {text.Trim()}";
        lock (Sync)
        {
            NativeLogRing.Enqueue(line);
            while (NativeLogRing.Count > NativeLogRingCapacity)
            {
                NativeLogRing.Dequeue();
            }
        }
    }

    public static void Configure(AppOptions options, ILogger logger)
    {
        lock (Sync)
        {
            if (_configured)
            {
                return;
            }

            string requested = (options.Runtime ?? "auto").Trim().ToLowerInvariant();
            RuntimePreference = requested;

            NvidiaProbe probe = ProbeNvidia();
            NvidiaDetected = probe.Detected;
            NvidiaDescription = probe.Description;
            NvidiaDetectionMethod = probe.Method;

            switch (requested)
            {
                case "auto":
                    // CRITICAL: this is intentionally identical to the original application.
                    // Do not touch RuntimeOptions.RuntimeLibraryOrder in auto mode. The user's
                    // original program simply called WhisperFactory.FromPath(path), and that
                    // automatic native-runtime selection is the compatibility baseline.
                    UseGpu = true;
                    RequireGpu = NvidiaDetected;
                    break;

                case "gpu":
                case "nvidia":
                    // Keep Whisper.net's own default GPU/runtime selection, but require that the
                    // selected runtime is GPU-capable. This is intentionally different from
                    // --runtime cuda, which really forces CUDA only.
                    UseGpu = true;
                    RequireGpu = true;
                    break;

                case "cuda":
                case "cuda12":
                    // Explicit opt-in only. Whisper.net 1.8.1 exposes one CUDA runtime, built
                    // against CUDA 12.x. Do not force this path for --runtime auto.
                    UseGpu = true;
                    RequireGpu = true;
                    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda];
                    break;

                case "vulkan":
                    UseGpu = true;
                    RequireGpu = true;
                    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan];
                    break;

                case "cpu":
                    UseGpu = false;
                    RequireGpu = false;
                    RuntimeOptions.RuntimeLibraryOrder =
                    [
                        RuntimeLibrary.Cpu,
                        RuntimeLibrary.CpuNoAvx
                    ];
                    break;

                case "cuda13":
                    throw new ArgumentException(
                        "This compatibility build intentionally uses Whisper.net 1.8.1, whose CUDA package is CUDA 12.x. " +
                        "Use --runtime auto/cuda instead; cuda13 would require upgrading the Whisper runtime again.",
                        nameof(options.Runtime));

                default:
                    throw new ArgumentException(
                        $"Unknown Whisper runtime '{options.Runtime}'. Use auto, gpu, cuda, cuda12, vulkan, or cpu.",
                        nameof(options.Runtime));
            }

            _configured = true;

            if (NvidiaDetected)
            {
                logger.Information(
                    "NVIDIA GPU detected ({method}): {gpu}",
                    NvidiaDetectionMethod,
                    string.IsNullOrWhiteSpace(NvidiaDescription)
                        ? "device present; detailed nvidia-smi query unavailable"
                        : NvidiaDescription);
            }
            else
            {
                logger.Warning(
                    "No NVIDIA GPU was detected by nvidia-smi, /dev/nvidia*, or /proc/driver/nvidia. Runtime auto may use CPU.");
            }


            logger.Information(
                "Whisper runtime preference: {runtime}; compatibility package=Whisper.net 1.8.1; GPU device={gpuDevice}; GPU required={requireGpu}; runtime order={order}",
                RuntimePreference,
                options.GpuDevice,
                RequireGpu,
                string.Join(" -> ", RuntimeOptions.RuntimeLibraryOrder));

            if (RuntimePreference == "auto")
            {
                logger.Information("Runtime auto compatibility mode: Whisper.net RuntimeLibraryOrder was left untouched (same selection path as the original application).");
            }
        }
    }

    public static WhisperFactory CreateFactory(string modelPath, AppOptions options)
    {
        // For the normal/default case use the exact overload used by the original application.
        // This intentionally leaves every 1.8.1 factory default untouched.
        if (RuntimePreference == "auto" && options.GpuDevice == 0 && UseGpu)
        {
            return WhisperFactory.FromPath(modelPath);
        }

        return WhisperFactory.FromPath(modelPath, CreateFactoryOptions(options));
    }

    public static WhisperFactoryOptions CreateFactoryOptions(AppOptions options)
    {
        // Match Whisper.net 1.8.1's original defaults, changing only what the user explicitly
        // requested. In particular UseGpu=true is the same default used by FromPath(path).
        WhisperFactoryOptions factoryOptions = WhisperFactoryOptions.Default;
        factoryOptions.UseGpu = UseGpu;
        factoryOptions.GpuDevice = options.GpuDevice;
        return factoryOptions;
    }

    public static void ValidateLoadedRuntime(ILogger logger)
    {
        RuntimeLibrary? loaded = RuntimeOptions.LoadedLibrary;
        if (loaded is null)
        {
            logger.Debug("Whisper native runtime has not been loaded yet.");
            return;
        }

        bool gpuLoaded = loaded.Value == RuntimeLibrary.Cuda ||
                         loaded.Value == RuntimeLibrary.Vulkan;

        lock (Sync)
        {
            if (!_runtimeLogged)
            {
                _runtimeLogged = true;
                logger.Information(
                    "Whisper native runtime loaded: {runtime}; GPU acceleration active={gpuActive}",
                    loaded,
                    gpuLoaded && UseGpu);
                logger.Information(
                    "Whisper.net compatibility runtime verification: LoadedLibrary={runtime} (1.8.1).",
                    loaded);
            }
        }

        if (RequireGpu && (!gpuLoaded || !UseGpu))
        {
            DumpNativeLoaderDiagnostics(logger);
            throw new WhisperRuntimeUnavailableException(
                $"GPU acceleration was requested, but Whisper.net 1.8.1 loaded '{loaded}', which this build does not classify as a GPU backend. " +
                "In --runtime auto mode the compatibility build deliberately preserves Whisper.net's original runtime selection instead of forcing CUDA. " +
                "Use --runtime cuda only when you explicitly want CUDA-only loading.");
        }
    }

    public static void DumpNativeLoaderDiagnostics(ILogger logger)
    {
        string[] snapshot;
        lock (Sync)
        {
            snapshot = NativeLogRing.ToArray();
        }

        if (snapshot.Length == 0)
        {
            logger.Warning("No Whisper native-loader diagnostic lines were captured.");
            return;
        }

        logger.Warning("Whisper native-loader diagnostics (most recent {count} lines):", snapshot.Length);
        foreach (string line in snapshot)
        {
            logger.Warning("[Whisper native] {line}", line);
        }
    }

    private static NvidiaProbe ProbeNvidia()
    {
        string? listOutput = TryRunNvidiaSmi(["-L"], 2500);
        bool smiDetected = !string.IsNullOrWhiteSpace(listOutput) &&
                           listOutput.Contains("GPU ", StringComparison.OrdinalIgnoreCase);

        if (smiDetected)
        {
            string? query = TryRunNvidiaSmi(
                ["--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits"],
                2500);
            string? description = FormatNvidiaQuery(query) ??
                                  listOutput!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .FirstOrDefault();
            return new NvidiaProbe(true, description, "nvidia-smi");
        }

        try
        {
            if (Directory.Exists("/proc/driver/nvidia/gpus") &&
                Directory.EnumerateDirectories("/proc/driver/nvidia/gpus").Any())
            {
                return new NvidiaProbe(true, null, "/proc/driver/nvidia/gpus");
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl"))
            {
                return new NvidiaProbe(true, null, "/dev/nvidia*");
            }
        }
        catch
        {
        }

        return new NvidiaProbe(false, null, "none");
    }

    private static string? FormatNvidiaQuery(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        string[] parts = first.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length >= 3
            ? $"{parts[0]} (driver {parts[1]}, {parts[2]} MiB VRAM)"
            : first;
    }

    private static string? TryRunNvidiaSmi(IReadOnlyList<string> arguments, int timeoutMs)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return null;
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            return stdout.Trim();
        }
        catch
        {
            return null;
        }
    }

    private sealed record NvidiaProbe(bool Detected, string? Description, string Method);
}
