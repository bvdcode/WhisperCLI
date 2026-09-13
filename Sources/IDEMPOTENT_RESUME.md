# v16: terminal checkpoint outcomes and idempotent resume

## Normal behavior

An unchanged input, primary model and decoding/recovery options reuse all terminal
checkpoint entries. `NeedsReview=true` means that processing finished but the text
needs review. It no longer means "repeat the entire recovery ladder next time".
No text is silently declared correct or removed to achieve this.

A completed run with a compatible checkpoint and final `.transcription.json` report
returns before model resolution, CUDA dependency preflight, FFmpeg normalization,
chunk planning and Whisper inference. Existing complete output files are left alone;
missing exports are regenerated from the saved result without inference.

Cache identity uses input path, byte length, last-write time and the existing
options fingerprint. This is metadata-based identity, not a cryptographic hash
of the entire audio. Deliberately replacing bytes while preserving both size and
mtime is outside this policy. Changing a decoding/recovery option can invalidate
reuse. LargeV1, LargeV2 and LargeV3 remain separate primary-model namespaces.

Partial runs process missing intervals only. Failed native/backend attempts and
malformed entries remain non-reusable. Review flags are propagated from children
and saved after the final audit, including merged-parent quality failures.

## Explicit repair

- `--retry-review`: retry cached review-marked outcomes once during this invocation.
- `--revalidate-checkpoint`: rescore cached text with the current detector and retry
  suspicious outcomes. This is an explicit improvement pass, not ordinary resume.
- `--resume false`: ignore checkpoints and perform a fresh run.

The default is false for both new flags. They are operational controls and do not
change the existing fingerprint. First-time chunk quality checks, normal fallback
models and bounded recursive recovery are unchanged. The end-of-run cross-chunk
repair pass runs only when new chunks were processed and does not automatically
restart a terminal review-marked outcome.

"Completed-with-review" is a finished processing state, not an accurate transcript
certification. Review files remain visible on cached runs. In particular, the final
review file now lists a suspicious merged parent even when its children each passed
in isolation.

## v15 compatibility and interrupted revalidation

Schema 9 and 10 checkpoints remain readable; this update does not invalidate them.
Model-scoped naming and legacy unqualified migration remain supported.

v14/v15 could remove terminal entries from the checkpoint before retranscription,
then be interrupted. The final `.transcription.json` may still contain the last
completed run. v16 can restore removed entries from that report only when:

1. Its model, input path and full contiguous timeline match.
2. The checkpoint matches the current source metadata and options fingerprint.
3. Every retained checkpoint entry agrees exactly with the completed report, so
   results from a newer interrupted repair cannot be rolled back.
4. New reports carry matching source size/mtime/fingerprint. Legacy reports instead
   require a corroborating nonempty checkpoint and consistent file/run timestamps.

A qualified checkpoint changed by this recovery is backed up once as
`*.transcription.checkpoint.json.pre-v16.bak`. Unqualified legacy files are never
overwritten during migration. A bare TXT file is never enough to declare a run
complete. If the report is missing, partial, inconsistent or cannot be matched,
normal checkpoint resume is used rather than guessing. A first migration can
update report metadata or regenerate exports; subsequent unchanged cache hits do
not rewrite output bytes or timestamps.

## Folder batch

From the Sources directory:

```bash
bash scripts/transcribe-folder.sh "/path/to/folder"
```

The helper builds once, then processes top-level `.mp4` files case-insensitively
with LargeV1, LargeV2 and LargeV3 sequentially. Whitespace, Cyrillic and newline
characters in filenames are preserved. It disables clipboard copying and the CLI's
10-second exit delay, and stops on errors or Ctrl+C instead of starting the next
model. `NeedsReview` is not an execution error; these outcomes remain recorded.

Optional explicit repair pass:

```bash
bash scripts/transcribe-folder.sh "/path/to/folder" --retry-review
```

For fair *single-model* comparisons rather than primary-plus-fallback runs, use
`--fallback-models none`. That changes the settings fingerprint and is intentionally
not the default in this batch helper.

## Regression checks

```bash
dotnet run --project tests/CheckpointRegression/CheckpointRegression.csproj
```

The regression harness exercises the actual C# cache code, terminal/review states,
full-coverage checks, legacy migration, removed-entry recovery, zero-inference second
runs, unchanged output timestamps, explicit repair controls, changed input/options,
backend failure exclusion and parent-only review output. It requires the .NET 9 SDK
and a package restore, but does not use a GPU, Whisper model, FFmpeg or real audio.
The resolver throws if inference is accidentally reached.

The nested test program is stored as `.cs.txt` and explicitly included by its test
project. The CLI excludes `tests/**/*.cs` so nested generated assembly attributes
cannot contaminate its build.

The delivery environment could not install a .NET SDK (download/DNS restrictions),
so the C# build and this harness were not executed there. Separate package/patch,
source-wiring, existing-artifact consistency and Bash batch smoke tests were run.
CUDA locator, native worker, model packages and decoding settings are unchanged.
