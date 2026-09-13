#!/usr/bin/env bash
# Nonrecursive, sequential LargeV1/LargeV2/LargeV3 batch. Uses the CLI checkpoint policy.
# Usage: bash scripts/transcribe-folder.sh "/path/to/folder" [extra CLI options]
set -euo pipefail
if (( $# < 1 )); then
    printf 'Usage: %s FOLDER [extra CLI options]\n' "$0" >&2
    exit 2
fi
directory=$1
shift
if [[ ! -d "$directory" ]]; then
    printf 'Folder does not exist: %s\n' "$directory" >&2
    exit 2
fi
# Absolute directory makes a leading '-' in a relative path harmless to find.
directory=$(cd -L -- "$directory" && pwd -L)
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
project="$script_directory/../WhisperCLI.csproj"
manifest=$(mktemp)
trap 'rm -f -- "$manifest"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
find "$directory" -maxdepth 1 -type f -iname '*.mp4' -print0 | sort -z > "$manifest"
if [[ ! -s "$manifest" ]]; then
    printf 'No MP4 files in: %s\n' "$directory"
    exit 0
fi
dotnet build "$project" --nologo
while IFS= read -r -d '' file; do
    for model in LargeV1 LargeV2 LargeV3; do
        printf '\n=== %s :: %s ===\n' "$model" "$file"
        if dotnet run --project "$project" --no-build -- \
            -m "$model" --copy-to-clipboard false --delay-seconds 0 "$file" "$@" < /dev/null; then
            :
        else
            status=$?
            printf 'Batch stopped: %s :: %s (exit %d)\n' "$model" "$file" "$status" >&2
            exit "$status"
        fi
    done
done < "$manifest"
