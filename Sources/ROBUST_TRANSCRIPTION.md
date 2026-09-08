# Robust long-file transcription — v5

This build keeps the robust long-file pipeline while restoring the Whisper.net runtime generation
from the original GPU-working application.

## Important GPU compatibility decision

The original project used:

```xml
<PackageReference Include="Whisper.net" Version="1.8.1" />
<PackageReference Include="Whisper.net.AllRuntimes" Version="1.8.1" />
```

v5 restores those exact versions. This is intentional. Whisper.net 1.8.1's CUDA runtime requires
CUDA Toolkit 12.1+, while the later 1.9.1 packages changed the CUDA runtime requirements. Upgrading
Whisper.net caused this application to select CPU on a machine where the original 1.8.1 build had
already been proven to use the NVIDIA GPU.

For the default `--runtime auto` path, v5 also uses the same factory overload as the original code:

```csharp
WhisperFactory.FromPath(modelPath)
```

so Whisper.net 1.8.1 keeps its own original runtime order and factory defaults (`UseGpu=true`).

A correct v5 startup prints:

```text
WhisperCLI robust build: v6-vulkan-gpu-compatible
Whisper runtime preference: auto; compatibility package=Whisper.net 1.8.1; ...
```

After the first model is loaded, look for:

```text
Whisper native runtime loaded: Cuda; GPU acceleration active=True
```

## Why Silero VAD is no longer used

Silero VAD support was added to Whisper.net after 1.8.1. Rather than keep a newer Whisper native
runtime just for VAD, v5 uses a small managed energy/silence detector over the normalized PCM16 WAV.
It is deliberately **only a chunk-boundary hint**. It never decides that audio should be deleted or
skipped. Every coarse audio chunk is still sent to Whisper.

This preserves the important behavior: chunk boundaries prefer quiet gaps, while a detector mistake
cannot silently remove a quiet sentence.

`--use-vad false` disables this managed boundary detector and uses fixed-duration chunks.

## Recommended command

```bash
dotnet run -- -m LargeV3 "/path/to/recording.mp3"
```

If the language is known and you are starting a fresh job, specifying it is normally preferable:

```bash
dotnet run -- -m LargeV3 --language ru "/path/to/recording.mp3"
```

If you already have a checkpoint created with `--language auto`, keep `auto` until that recording is
finished so the existing checkpoint fingerprint remains compatible.

## Robust pipeline

1. Normalize input once to 16 kHz mono PCM16 WAV.
2. Detect quiet gaps with the managed boundary detector.
3. Split the complete timeline into independent coarse chunks (75 s target, 90 s hard maximum).
4. Give every chunk a fresh `WhisperProcessor`, preventing a bad decoder history from poisoning the
   rest of a long recording.
5. Limit previous-text conditioning to 64 tokens on the primary attempt.
6. Watch generated segments for obvious repetition loops and abort a bad attempt early.
7. If a chunk is suspicious, retry automatically:
   - same model with `WithNoContext()`;
   - same model with a shifted/wider audio boundary;
   - fallback Large model(s), loaded lazily;
   - recursive split near a quiet gap if direct retries still fail.
8. Keep only one Large model loaded at a time to avoid exhausting GPU VRAM.
9. Save a checkpoint and `.partial.*` outputs after every completed top-level chunk.
10. Merge accepted segments by absolute timestamps into TXT/SRT/VTT.

Whisper.net 1.8.1 already provides both `WithMaxLastTextTokens()` and `WithNoContext()`, so the
anti-loop strategy does not require a newer Whisper.net version.

## Resume behavior

v5 preserves a contiguous prefix of completed chunks from the v4 checkpoint when the fingerprint
matches, even though future pause boundaries are now produced by the managed detector. For the
recording used during development, this means completed work through approximately `00:06:17.650`
can remain reusable while only the unprocessed suffix is replanned.

## Runtime options

```text
--runtime auto      Preserve Whisper.net 1.8.1's original automatic runtime selection (default)
--runtime gpu       Require the 1.8.1 CUDA runtime
--runtime cuda      Same as gpu
--runtime cuda12    Alias for cuda in this compatibility build
--runtime cpu       Intentionally use CPU
--gpu-device N      Select GPU device (default 0)
```

`cuda13` is intentionally rejected in v5 because supporting it would require moving back to the
newer runtime generation that caused the regression.

## Incremental output and cancellation

After each completed chunk:

```text
recording.partial.txt
recording.partial.srt
recording.partial.vtt
recording.transcription.checkpoint.json
```

First `Ctrl+C` requests cooperative cancellation and preserves completed chunks. A second `Ctrl+C`
allows immediate OS termination if native inference does not return promptly.

On successful completion:

```text
recording.txt
recording.srt
recording.vtt
recording.transcription.json
recording.transcription.log
```

`recording.transcription.review.txt` is created only when an interval remains questionable after the
recovery ladder is exhausted.

## Fallback models

Default `--fallback-models auto`:

- `LargeV3Turbo` -> `LargeV3`, then `LargeV2`
- `LargeV3` -> `LargeV3Turbo`, then `LargeV2`
- `LargeV2` -> `LargeV3`, then `LargeV3Turbo`

Fallback model files are only resolved when a suspicious chunk actually reaches that stage.

## Useful options

```text
--language ru
--fallback-models auto
--fallback-models none
--chunk-seconds 75
--max-chunk-seconds 90
--max-context-tokens 64
--entropy-threshold 2.7
--glitch-threshold 0.65
--max-recovery-depth 2
--resume false
--use-vad false
-v
```

## FFmpeg

Xabe is retained for downloading FFmpeg. Normalization itself invokes FFmpeg with
`ProcessStartInfo.ArgumentList`, so paths containing spaces and Cyrillic are passed without shell
quoting problems.


## v6 runtime correction

Whisper.net 1.8.1 can use either CUDA or Vulkan as a GPU backend. In `--runtime auto`, both are accepted as GPU acceleration. A runtime-selection failure is treated as fatal and is not fed into the transcription recovery ladder.
