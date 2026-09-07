using Serilog;
using System.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace WhisperCLI;

internal static class WhisperRuntimeManager
{
    private static readonly object Sync = new();
    private static bool _configured;
    private static bool _runtimeLogged;

    public static bool NvidiaDetected { get; private set; }
    public static bool RequireGpu { get; private set; }
    public static bool UseGpu { get; private set; } = true;
    public static string RuntimePreference { get; private set; } = "auto";
    public static string? NvidiaDescription { get; private set; }
    public static string NvidiaDetectionMethod { get; private set; } = "none";

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
                    UseGpu = true;
                    if (NvidiaDetected)
                    {
                        RuntimeOptions.RuntimeLibraryOrder =
                        [
                            RuntimeLibrary.Cuda,
                            RuntimeLibrary.Cuda12,
                            RuntimeLibrary.Cpu,
                            RuntimeLibrary.CpuNoAvx
                        ];
                        RequireGpu = true;
                    }
                    break;

                case "gpu":
                case "nvidia":
                    UseGpu = true;
                    RequireGpu = true;
                    RuntimeOptions.RuntimeLibraryOrder =
                    [
                        RuntimeLibrary.Cuda,
                        RuntimeLibrary.Cuda12
                    ];
                    break;

                case "cuda":
                case "cuda13":
                    UseGpu = true;
                    RequireGpu = true;
                    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda];
                    break;

                case "cuda12":
                    UseGpu = true;
                    RequireGpu = true;
                    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda12];
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

                default:
                    throw new ArgumentException(
                        $"Unknown Whisper runtime '{options.Runtime}'. Use auto, gpu, cuda, cuda12, or cpu.",
                        nameof(options.Runtime));
            }

            _configured = true;

            if (NvidiaDetected)
            {
                logger.Information(
                    "NVIDIA GPU detected ({method}): {gpu}",
                    NvidiaDetectionMethod,
                    string.IsNullOrWhiteSpace(NvidiaDescription) ? "device present; detailed nvidia-smi query unavailable" : NvidiaDescription);
            }
            else
            {
                logger.Warning(
                    "No NVIDIA GPU was detected by nvidia-smi, /dev/nvidia*, or /proc/driver/nvidia. Runtime auto may use CPU.");
            }

            logger.Information(
                "Whisper runtime preference: {runtime}; GPU device={gpuDevice}; GPU required={requireGpu}; runtime order={order}",
                RuntimePreference,
                options.GpuDevice,
                RequireGpu,
                string.Join(" -> ", RuntimeOptions.RuntimeLibraryOrder));
        }
    }

    public static WhisperFactoryOptions CreateFactoryOptions(AppOptions options) => new()
    {
        UseGpu = UseGpu,
        GpuDevice = options.GpuDevice
    };

    public static void ValidateLoadedRuntime(ILogger logger)
    {
        RuntimeLibrary? loaded = RuntimeOptions.LoadedLibrary;
        if (loaded is null)
        {
            logger.Debug("Whisper native runtime has not been loaded yet.");
            return;
        }

        bool gpuLoaded = loaded == RuntimeLibrary.Cuda || loaded == RuntimeLibrary.Cuda12;

        lock (Sync)
        {
            if (!_runtimeLogged)
            {
                _runtimeLogged = true;
                string info;
                try
                {
                    info = WhisperFactory.GetRuntimeInfo()?.Trim() ?? "<not reported>";
                }
                catch (Exception ex)
                {
                    info = $"<failed to query: {ex.Message}>";
                }

                logger.Information(
                    "Whisper native runtime loaded: {runtime}; GPU acceleration active={gpuActive}",
                    loaded, gpuLoaded && UseGpu);
                logger.Information("Whisper native system info: {runtimeInfo}", info.Replace('\n', ' ').Replace('\r', ' '));
            }
        }

        if (RequireGpu && (!gpuLoaded || !UseGpu))
        {
            throw new InvalidOperationException(
                $"An NVIDIA GPU was requested/detected, but Whisper.net loaded '{loaded}' instead of a CUDA runtime. " +
                "WhisperCLI refuses to silently continue with Large-model CPU inference. " +
                "Note that 'CUDA Version' in nvidia-smi is the maximum CUDA version supported by the DRIVER; " +
                "it does not prove that the CUDA Toolkit/runtime libraries are installed. " +
                "Whisper.net 1.9.1 requires CUDA Toolkit >= 13.0.1 for --runtime cuda, or >= 12.4.1 for --runtime cuda12. " +
                "Check 'nvcc --version' and the presence of libcudart/libcublas, install the required toolkit, " +
                "or explicitly use --runtime cpu if CPU inference is intentional.");
        }
    }

    private static NvidiaProbe ProbeNvidia()
    {
        // Prefer nvidia-smi -L because it avoids querying optional telemetry fields
        // (temperature/power/utilization), which may show ERR! on some laptop/driver combinations.
        string? listOutput = TryRunNvidiaSmi(["-L"], 2500);
        bool smiDetected = !string.IsNullOrWhiteSpace(listOutput) &&
                           listOutput.Contains("GPU ", StringComparison.OrdinalIgnoreCase);

        string? description = null;
        if (smiDetected)
        {
            string? query = TryRunNvidiaSmi(
                ["--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits"],
                2500);
            description = FormatNvidiaQuery(query) ?? listOutput!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
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
            // Continue with device-node fallback.
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
            // Treat as not detected.
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
