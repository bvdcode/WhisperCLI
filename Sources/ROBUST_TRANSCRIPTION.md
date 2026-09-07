# Robust long-file transcription

This build changes file transcription from one continuous Whisper decode into an adaptive,
checkpointed pipeline intended for long recordings where Whisper occasionally enters a
repetition/hallucination loop.

Microphone mode remains lightweight. The robust pipeline is used when an input file path is
provided.

## Recommended commands

For a Russian recording, explicitly fix the language when you know it:

```bash
dotnet run -- "recording.mp3" --language ru -m LargeV3Turbo
```

For automatic language detection:

```bash
dotnet run -- "recording.mp3" -m LargeV3Turbo
```

When running an already built executable, replace `dotnet run --` with the executable name,
for example:

```bash
WhisperCLI "recording.mp3" --language ru -m LargeV3Turbo
```

## What happens automatically

1. The input is converted once to 16 kHz mono PCM WAV.
2. Silero VAD finds speech/silence boundaries.
3. The complete recording timeline is divided into independent coarse chunks (75 s target, 90 s maximum).
   VAD is used to choose safe boundaries; it is not used to concatenate tiny speech fragments.
4. Silence-only chunks are skipped instead of being sent to Whisper, reducing silence
   hallucinations.
5. Each speech chunk gets a fresh `WhisperProcessor` so decoder history cannot poison the
   rest of a long recording.
6. The normal pass keeps only 64 previous-text tokens inside that chunk.
7. Output is continuously checked for repetition loops. An obvious loop aborts the current
   attempt early.
8. Suspicious chunks are retried automatically:
   - same model with previous-text conditioning disabled;
   - same model with a shifted/wider audio boundary;
   - lazily loaded fallback Large models;
   - recursive split at a VAD silence gap if all direct attempts are still suspicious.
9. Only one Large Whisper model is kept loaded at a time to avoid exhausting GPU VRAM.
10. Completed top-level chunks are checkpointed after every chunk, so an interrupted run can
    resume without retranscribing completed work.
11. Accepted segments are merged by absolute timestamps; no ChatGPT stitching step is needed.

## Default model recovery

`--fallback-models auto` is the default.

- `LargeV3Turbo` -> `LargeV3`, then `LargeV2`
- `LargeV3` -> `LargeV3Turbo`, then `LargeV2`
- `LargeV2` -> `LargeV3`, then `LargeV3Turbo`

Fallback model files are resolved/downloaded only if a suspicious chunk actually reaches that
stage. A fallback is never loaded alongside another Large model.

Disable model fallback:

```bash
WhisperCLI "recording.mp3" --fallback-models none
```

Choose fallbacks explicitly:

```bash
WhisperCLI "recording.mp3" --fallback-models LargeV3,LargeV2
```

## Output files

For `recording.mp3`, the application writes:

- `recording.txt` - final merged transcript
- `recording.srt` - timestamped subtitles
- `recording.vtt` - timestamped WebVTT
- `recording.transcription.json` - complete machine-readable run diagnostics
- `recording.transcription.log` - human-readable chunk/attempt diagnostics
- `recording.transcription.checkpoint.json` - resumable progress
- `recording.transcription.review.txt` - created only when automatic recovery is exhausted for
  one or more intervals

If `recording.transcription.review.txt` does not exist after a successful run, no interval was
left flagged by the automatic quality detector.

## Useful options

```text
--language ru                    Fix language when known (recommended)
--language auto                  Automatic language detection
--fallback-models auto           Automatic fallback Large models (default)
--fallback-models none           Disable model fallback
--chunk-seconds 75               Target independent chunk duration
--max-chunk-seconds 90           Hard chunk/VAD speech-region maximum
--max-context-tokens 64          Normal-pass previous-text context limit
--entropy-threshold 2.7          Whisper entropy fallback threshold
--glitch-threshold 0.65          Automatic rejection threshold
--max-recovery-depth 2           Recursive recovery split depth
--resume false                   Ignore an existing matching checkpoint
--use-vad false                  Disable VAD and use fixed-duration chunks
--lock-detected-language false   Keep re-detecting language in auto mode
-v                               Verbose per-segment diagnostics
```

Options that default to true accept an explicit value, for example `--resume false` or
`--use-vad false`.

## If a run still has a bad interval

Send both of these files with the original problematic interval description:

```text
recording.transcription.log
recording.transcription.json
```

They record the exact chunk timestamps, model and recovery strategy selected, rejected
attempts, repetition score/reasons, elapsed time, and whether the live loop detector aborted an
attempt. This should make tuning a specific failure reproducible instead of relying on manual
trial-and-error.

## FFmpeg normalization implementation

FFmpeg is downloaded through `Xabe.FFmpeg.Downloader`, but audio normalization is invoked directly
with `System.Diagnostics.Process` and `ProcessStartInfo.ArgumentList`. This deliberately avoids the
Xabe conversion argument builder for the `-i`/output command. It is safe for paths containing spaces
or non-ASCII characters and, on failure, the exception now includes the useful tail of FFmpeg stderr.

## GPU runtime selection (v3)

WhisperCLI now makes native-runtime selection visible. On startup it probes `nvidia-smi`.

- `--runtime auto` (default): if an NVIDIA GPU is detected, CUDA is expected. The application refuses to silently run Large models on CPU if Whisper.net falls back to a CPU runtime.
- `--runtime gpu`: allow CUDA 13 or CUDA 12 only.
- `--runtime cuda`: force the CUDA 13 Whisper.net runtime.
- `--runtime cuda12`: force the CUDA 12 Whisper.net runtime.
- `--runtime cpu`: intentionally use CPU.
- `--gpu-device N`: select the GPU device index passed to Whisper (default 0).

Whisper.net 1.9.1 provides both CUDA 13 and CUDA 12 runtimes. The host must still have the matching NVIDIA runtime/toolkit libraries available. If CUDA cannot be loaded, WhisperCLI now stops with an explicit error instead of spending minutes per chunk on accidental CPU inference.

Example:

```bash
dotnet run -- -m LargeV3 --runtime cuda12 --language ru "/path/to/recording.mp3"
```

The startup log prints the detected NVIDIA GPU, the selected `RuntimeLibrary`, and Whisper's native system information. `GPU acceleration active=True` is the line to look for.

## Incremental results and cancellation (v3)

Long-file transcription now writes usable progress after every completed top-level chunk:

```text
recording.partial.txt
recording.partial.srt
recording.partial.vtt
recording.transcription.checkpoint.json
```

These files live next to the input recording. They are rebuilt from the checkpoint when a run resumes, so completed chunks remain readable even if the process is interrupted. On successful completion the final `recording.txt/.srt/.vtt` files are written and the `.partial.*` files are removed.

Cancellation is two-stage:

1. First `Ctrl+C`: request cooperative cancellation and preserve all completed chunks/checkpoints.
2. Second `Ctrl+C`: terminate immediately if native inference does not return promptly.

## v4 installation note

The `WhisperCLI_Robust_Transcription_v4_SourcesOverlay.zip` archive is intentionally rooted at the contents of the `Sources` directory. Extract it **while inside your existing `WhisperCLI/Sources` directory**. At startup, a correct v4 installation prints:

```
WhisperCLI robust build: v4-gpu-runtime
Whisper runtime preference: ...
```

If those lines are absent, you are running an older source tree/build.
