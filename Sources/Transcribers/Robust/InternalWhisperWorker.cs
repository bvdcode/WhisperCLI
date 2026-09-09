using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace WhisperCLI.Transcribers.Robust;

internal sealed class WhisperWorkerRequest
{
    public string ModelPath { get; set; } = string.Empty;
    public string AudioPath { get; set; } = string.Empty;
    public string Language { get; set; } = "auto";
    public string Runtime { get; set; } = "auto";
    public int GpuDevice { get; set; }
    public bool NoContext { get; set; }
    public float EntropyThreshold { get; set; } = 2.7f;
    public float Temperature { get; set; }
    public float TemperatureIncrement { get; set; } = 0.2f;
    public string ResultPath { get; set; } = string.Empty;
}

internal sealed class WhisperWorkerResult
{
    public string? LoadedRuntime { get; set; }
    public bool LiveLoopAborted { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = [];
    public string? ManagedError { get; set; }
}

internal static class InternalWhisperWorker
{
    public const string Switch = "--internal-transcription-worker";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static bool IsWorkerInvocation(string[] args) =>
        args.Length >= 2 && string.Equals(args[0], Switch, StringComparison.Ordinal);

    public static async Task<int> RunAsync(string requestPath)
    {
        WhisperWorkerResult result = new();
        try
        {
            WhisperWorkerRequest request = JsonSerializer.Deserialize<WhisperWorkerRequest>(
                await File.ReadAllTextAsync(requestPath), JsonOptions)
                ?? throw new InvalidOperationException("Worker request JSON is empty.");

            ValidateRequest(request);
            ConfigureRuntime(request);

            if (OperatingSystem.IsLinux() && request.Runtime.Trim().Equals("cuda", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[worker] CUDA-only guard active: CUDA_VISIBLE_DEVICES={Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") ?? "<unset>"}, " +
                    $"LD_LIBRARY_PATH={Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "<unset>"}");
            }

            // Keep native diagnostics out of stdout: stdout is intentionally unused by the
            // worker so the parent can safely capture both streams for crash diagnostics.
            LogProvider.AddLogger((level, text) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Console.Error.WriteLine($"[Whisper/{level}] {text.Trim()}");
                }
            });

            using WhisperFactory factory = CreateFactory(request);
            result.LoadedRuntime = RuntimeOptions.LoadedLibrary?.ToString();
            Console.Error.WriteLine($"[worker] loaded runtime: {result.LoadedRuntime ?? "<unknown>"}");

            // PRIMARY PATH IS INTENTIONALLY THE ORIGINAL APPLICATION PATH:
            // language + default decoding. The robust parent already limits damage by giving
            // each attempt an independent real WAV file, so there is no need to perturb the
            // normal decoder. Recovery attempts may explicitly request NoContext and fallback
            // decoding settings.
            var builder = factory.CreateBuilder().WithLanguage(request.Language);
            if (request.NoContext)
            {
                builder
                    .WithNoContext()
                    .WithEntropyThreshold(request.EntropyThreshold)
                    .WithTemperature(request.Temperature)
                    .WithTemperatureInc(request.TemperatureIncrement);
            }

            await using WhisperProcessor processor = builder.Build();
            await using FileStream audio = File.OpenRead(request.AudioPath);
            using CancellationTokenSource attemptCts = new();

            await foreach (var segment in processor.ProcessAsync(audio, attemptCts.Token))
            {
                result.Segments.Add(new TranscriptSegment
                {
                    Start = segment.Start,
                    End = segment.End,
                    Text = segment.Text,
                    Language = segment.Language
                });

                if (TranscriptionQualityAnalyzer.HasObviousLiveLoop(result.Segments))
                {
                    result.LiveLoopAborted = true;
                    attemptCts.Cancel();
                    break;
                }
            }

            await WriteResultAsync(request.ResultPath, result);
            return 0;
        }
        catch (OperationCanceledException) when (result.LiveLoopAborted)
        {
            // Cooperative abort after detecting an obvious repetition loop is a valid worker
            // outcome. The parent will score/retry the partial attempt.
            try
            {
                WhisperWorkerRequest request = JsonSerializer.Deserialize<WhisperWorkerRequest>(
                    await File.ReadAllTextAsync(requestPath), JsonOptions)!;
                await WriteResultAsync(request.ResultPath, result);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[worker] failed to persist live-loop result: {ex}");
                return 71;
            }
        }
        catch (Exception ex)
        {
            // Managed failures are serialized when possible. Native access violations/segfaults
            // never reach this catch; the parent detects those from the child process exit.
            Console.Error.WriteLine($"[worker] managed failure: {ex}");
            try
            {
                WhisperWorkerRequest? request = JsonSerializer.Deserialize<WhisperWorkerRequest>(
                    await File.ReadAllTextAsync(requestPath), JsonOptions);
                if (request is not null && !string.IsNullOrWhiteSpace(request.ResultPath))
                {
                    result.ManagedError = ex.ToString();
                    await WriteResultAsync(request.ResultPath, result);
                }
            }
            catch
            {
                // Preserve the original worker failure as the useful signal.
            }
            return 70;
        }
    }

    private static WhisperFactory CreateFactory(WhisperWorkerRequest request)
    {
        string runtime = request.Runtime.Trim().ToLowerInvariant();
        if (runtime == "auto" && request.GpuDevice == 0)
        {
            // This is the exact overload used by the original application.
            return WhisperFactory.FromPath(request.ModelPath);
        }

        WhisperFactoryOptions options = WhisperFactoryOptions.Default;
        options.UseGpu = runtime != "cpu";
        options.GpuDevice = request.GpuDevice;
        return WhisperFactory.FromPath(request.ModelPath, options);
    }

    private static void ConfigureRuntime(WhisperWorkerRequest request)
    {
        switch (request.Runtime.Trim().ToLowerInvariant())
        {
            case "auto":
            case "gpu":
            case "nvidia":
                // Deliberately leave Whisper.net's global default order untouched.
                break;
            case "cuda":
            case "cuda12":
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda];
                break;
            case "vulkan":
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan];
                break;
            case "cpu":
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                break;
            default:
                throw new ArgumentException($"Unsupported worker runtime '{request.Runtime}'.");
        }
    }

    private static void ValidateRequest(WhisperWorkerRequest request)
    {
        if (!File.Exists(request.ModelPath))
        {
            throw new FileNotFoundException("Worker model file not found.", request.ModelPath);
        }
        if (!File.Exists(request.AudioPath))
        {
            throw new FileNotFoundException("Worker audio file not found.", request.AudioPath);
        }
        if (string.IsNullOrWhiteSpace(request.ResultPath))
        {
            throw new ArgumentException("Worker result path is required.");
        }
    }

    private static Task WriteResultAsync(string path, WhisperWorkerResult result)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
    }
}
