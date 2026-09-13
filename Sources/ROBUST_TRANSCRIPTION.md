# Robust transcription — v16

**Current resume policy:** completed and completed-with-review results are terminal
and reused by default. Quality revalidation or retrying exhausted outcomes requires
`--revalidate-checkpoint` or `--retry-review`. See [IDEMPOTENT_RESUME.md](IDEMPOTENT_RESUME.md)
for the current behavior, compatibility checks, batch helper and regression tests.
The older sections below describe the implementation history; where they describe
automatic revalidation on every restart, the v16 policy supersedes them.

# Robust long-file transcription — v10

v10 keeps the chunk/retry/checkpoint pipeline but changes the most important reliability boundary: native Whisper inference runs in an isolated child process.

Why: whisper.cpp/Whisper.net GPU backends can terminate the entire .NET process with a native access violation/segfault/stack overflow. Such failures cannot be caught reliably by `try/catch`. In v10 the parent CLI survives, reports the worker exit code and native stderr tail, and never records the failed chunk as completed.

## v15 model-scoped output naming

Every persistent transcription artifact now includes the requested primary model in its
filename. The primary model is the CLI `-m/--model` value; fallback models selected for
individual chunks do not change the run namespace.

Example for `-m LargeV3` and input `lecture.mp3`:

- `lecture.LargeV3.txt`
- `lecture.LargeV3.srt` / `lecture.LargeV3.vtt`
- `lecture.LargeV3.partial.*`
- `lecture.LargeV3.transcription.checkpoint.json`
- `lecture.LargeV3.transcription.json` / `.log` / `.review.txt`


## Normal command

```bash
dotnet run -m LargeV3 "/path/to/recording.mp3"
```

For known Russian recordings you can use `--language ru` on a fresh run.

## Primary decoding path

For the first attempt of every chunk the child worker deliberately mirrors the old working application's Whisper setup:

- `WhisperFactory.FromPath(modelPath)` in `--runtime auto`;
- `factory.CreateBuilder().WithLanguage(...).Build()`;
- a normal FFmpeg-produced 16-kHz mono PCM16 WAV stream is passed to `ProcessAsync`. v10 stores that attempt WAV as a physical temporary file so it can validate it before crossing the native boundary.

No `WithMaxLastTextTokens`, temperature, or entropy override is applied to the normal pass. Robustness comes from independent chunk/process boundaries. More aggressive decoder settings are used only after a valid transcription is judged suspicious.

## Native crash isolation

Each transcription attempt runs as an internal child process. If a GPU backend crashes, the parent reports a message such as:

```text
Isolated Whisper worker terminated abnormally (exit code 139) ...
```

No checkpoint entry is written for that chunk. The run stops instead of manufacturing empty review chunks.

## Recovery ladder

For a valid but suspicious transcription:

1. primary model, original/default context;
2. primary model with `NoContext` and fallback decoding settings;
3. shifted boundaries with `NoContext`;
4. fallback large models for only that chunk;
5. recursive split when needed.

Execution/backend exceptions are never treated as transcription quality failures.

## Chunk files

The parent normalizes the original recording once, then creates each attempt WAV with FFmpeg. This avoids passing an NAudio-generated in-memory sub-WAV into native Whisper.

## Partial results and resume

After each accepted top-level chunk, outputs are namespaced by the requested primary model:

- `<name>.<PrimaryModel>.partial.txt`
- `<name>.<PrimaryModel>.partial.srt`
- `<name>.<PrimaryModel>.partial.vtt`
- `<name>.<PrimaryModel>.transcription.checkpoint.json`

Final artifacts use the same namespace, for example `recording.LargeV3.txt`,
`recording.LargeV3.srt`, `recording.LargeV3.transcription.json`, and
`recording.LargeV3.transcription.log`. This allows the same recording to be run with
LargeV3, LargeV3Turbo, LargeV2, etc. without overwriting another model's results.

For a one-time migration from v14 and older, if the model-scoped checkpoint does not
exist, v15 may read the legacy `<name>.transcription.checkpoint.json`. It is reused only
when its input/options fingerprint matches the requested primary model. The compatible
checkpoint is then written to the new model-scoped path; the legacy file is left untouched.

Ctrl+C kills the active worker and leaves already completed chunks reusable.

v10 uses checkpoint schema 6. Older schemas are intentionally ignored because they were produced before native-process isolation and may contain execution failures recorded as completed chunks.

## Runtimes

`--runtime auto` leaves Whisper.net 1.8.1 runtime selection untouched, matching the original application. Explicit `cpu`, `cuda`/`cuda12`, and `vulkan` modes remain available for diagnosis. On a machine where an NVIDIA GPU is detected, v10 refuses a silent CPU fallback unless `--runtime cpu` was explicitly requested.

## Clipboard

Missing Linux clipboard helpers such as `xsel` only produce a warning; transcription files remain successful.

## Performance note

v10 deliberately starts a fresh worker for each attempt. This adds model-load overhead, but it is the safest boundary while diagnosing the native GPU crash seen in v8. Once the backend is confirmed stable, the worker can be made persistent per model without changing the checkpoint/recovery design.


## Linux hybrid NVIDIA/Intel laptops

Whisper.net 1.8.1 may fall back from CUDA to Vulkan when `libcudart` is unavailable.
The bundled native whisper.cpp generation can ignore `GpuDevice` for Vulkan and use
the first enumerated Vulkan physical device. On Optimus laptops that can be the Intel
iGPU even when an NVIDIA GPU is present.

v10 scopes the following variables to each isolated Whisper worker in `auto`/`gpu`/
`nvidia` modes on Linux when NVIDIA is detected:

- `__NV_PRIME_RENDER_OFFLOAD=1`
- `__VK_LAYER_NV_optimus=NVIDIA_only`
- `GGML_VK_VISIBLE_DEVICES=0`
- `VK_DRIVER_FILES=<detected NVIDIA ICD>` and `VK_ICD_FILENAMES=<same>` when an NVIDIA ICD JSON is found

The NVIDIA PRIME Vulkan layer makes the NVIDIA device enumerate first; the GGML filter
is an additional safeguard on builds that support it. These variables are not set
globally and do not change the desktop session.

A healthy Vulkan worker on a hybrid laptop should log device 0 as the NVIDIA GPU.
If it still logs Intel as Vulkan device 0, v10 reports that explicitly and exits
without checkpointing the chunk.


## v11 Linux/NVIDIA CUDA dependency discovery

On Linux systems with an NVIDIA GPU, `auto` no longer falls back to Vulkan. Both the Intel and NVIDIA Vulkan paths on the tested RTX 4060 laptop terminated the native worker with SIGSEGV. v11 therefore discovers existing CUDA 12 runtime/cuBLAS libraries (`libcudart.so.12`, `libcublas.so.12`, and transitive dependencies), verifies the shipped `libggml-cuda-whisper.so` with `ldd`, augments the isolated worker's `LD_LIBRARY_PATH`, and runs CUDA only. It searches system/ldconfig paths, `/usr/local/cuda*`, Conda, and Python `nvidia-*` package locations. If dependencies are absent it fails before audio preprocessing with the exact missing `.so` names.

## v13 native-runtime copy repair

v13 fixes a build-order bug introduced by v12. v12 correctly pinned the runtime packages, but its custom MSBuild cleanup target could delete freshly copied Whisper native files from `bin/.../runtimes` after NuGet had staged them. Earlier builds temporarily
used Whisper.net 1.9.1 and were then returned to 1.8.1. Native Whisper libraries use the same filenames
between those versions, and incremental build output can retain a newer native `.so` after the managed
package is downgraded. That produces an ABI/runtime mixture (for example, a managed 1.8.1 build loading
a CUDA-13 or Linux-Vulkan native payload).

v13 therefore:

- pins `Whisper.net`, `Whisper.net.Runtime`, and `Whisper.net.Runtime.Cuda.Linux` to **exactly 1.8.1**;
- removes `Whisper.net.AllRuntimes` (and therefore removes Vulkan/OpenVINO/CoreML from the Linux file path);
- deletes stale Whisper native output files before `CopyFilesToOutputDirectory`, so the resolved package
  files are recopied even when an older/newer file has a misleading timestamp;
- checks the actual ELF dependencies with `ldd` and derives the required CUDA major from the binary;
- rejects a CUDA-13 native binary in this pinned 1.8.1 compatibility build as a stale/mixed build instead
  of telling the user to install CUDA 13.

After applying v13, perform one explicit clean rebuild (`rm -rf bin obj && dotnet restore --force`). v13 also has a development-time repair fallback: if `libggml-cuda-whisper.so` is absent from output, it searches the exact NuGet cache package `whisper.net.runtime.cuda.linux/1.8.1` and copies the native sibling `.so` files into `runtimes/cuda/linux-x64` before CUDA dependency preflight. Future normal
`dotnet run` builds rely on the exactly pinned native packages; the runtime repair fallback remains available if a development output is incomplete.


## v14 repetition-quality revalidation

v14 keeps the stable v13 CUDA/runtime architecture and tightens only transcription-quality handling. The completed 90-minute validation run exposed a false-negative class that v13 did not reject: long sentence cycles (the repeated unit can exceed 12 words) and two-copy segment hallucinations.

Changes:

- live loop detection searches repeating units up to 48 words and aborts after three exact repeats of a 4+ word phrase;
- final quality analysis also searches two-copy cycles up to 48 words;
- two identical long segments, and short chunks dominated by a repeated 3+ word segment, are treated as stronger hallucination signals;
- an extremely low unique-bigram ratio on a long chunk is a strong backstop for sentence-cycle loops;
- a 4+ word phrase repeated three times is now a strong signal instead of merely scoring 0.60 below the default 0.65 threshold;
- recursive split results remain marked for review if the merged transcript is still suspicious;
- schema-9 v13 checkpoints are accepted once, every cached final transcript is rescored with the v14 detector, bad cached chunks are removed, and only those intervals are retranscribed. Clean cached chunks remain reusable. The upgraded checkpoint is saved as schema 10.

This allows a finished v13 run to be repaired without retranscribing the full file.
