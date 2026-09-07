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
