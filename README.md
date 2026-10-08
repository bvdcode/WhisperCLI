# WhisperCLI

WhisperCLI is a command-line tool for transcribing audio from files or microphone input using OpenAI's Whisper speech recognition models via the Whisper.net library.

## Features

- Transcribe audio and video files to subtitles or text
- Generate subtitles for all media files in a folder
- Record and transcribe audio directly from microphone
- Support for various audio and video formats (mp3, mp4, mkv, avi, etc.)
- Automatic downloading of Whisper models
- Automatic downloading of FFmpeg
- Support for different Whisper model sizes (default: LargeV3Turbo)
- Standalone Windows x64 executable, with optional CUDA acceleration
- Progress reporting during conversion and transcription

## Requirements

- Windows x64 for the published executable; no separate .NET installation is required
- .NET SDK 10.0.301 for building from source

## Installation

### Using Published Release

Download `WhisperCLI-win-x64.exe` from the [latest release](https://github.com/bvdcode/WhisperCLI/releases/latest) and run it from your preferred location. The executable includes the .NET runtime and Whisper native libraries, which are extracted to the system temporary directory on first launch. Models and FFmpeg are downloaded as needed.

`SHA256SUMS` is included in each release to verify the executable's checksum.

### Building from Source

1. Clone the repository
2. Use the .NET SDK version pinned in `global.json`
3. Build the project:
   ```
   dotnet build Sources/WhisperCLI.sln
   ```
4. Publish the project (optional):
   ```
   dotnet publish Sources/WhisperCLI.csproj -p:PublishProfile=FolderProfile
   ```

## Usage

### Basic Usage

```
WhisperCLI [options] [inputFilePath]
```

### Command Line Options

- `-m, --model`: Model to use for transcription (default: LargeV3Turbo)
- `-l, --language`: Audio language code, such as `ru` or `en` (default: `auto`)
- `-i, --microphone-index`: Index of microphone to use for recording (default: 0)
- `-s, --stop-key`: Key to stop recording when using microphone input (default: Spacebar)
- `-f, --format`: Output format for file/folder transcription: `srt`, `vtt`, or `txt` (default: `srt`; microphone always writes `txt`)
- `--folder`: Process all media files in the specified folder
- `-r, --recursive`: Include subfolders when using `--folder`
- `inputFilePath`: Path to the audio/video file or folder to transcribe (if omitted without `--folder`, uses microphone input)

### Examples

```
# Transcribe an audio file
WhisperCLI input.mp3

# Transcribe Russian speech
WhisperCLI -l ru input.mp3

# Transcribe an audio file as plain text
WhisperCLI -f txt input.mp3

# Generate subtitles for all media files in the current folder
WhisperCLI --folder .

# Generate subtitles for all media files in a folder and its subfolders
WhisperCLI --folder "D:\Media" -r

# Transcribe a video file with a specific model
WhisperCLI -m Small video.mp4

# Record from microphone and transcribe
WhisperCLI

# Use a specific microphone (device index 2)
WhisperCLI -i 2

# Use a different key to stop recording (Enter key)
WhisperCLI -s Enter
```

### Available Models

- TinyEn
- Tiny
- BaseEn
- Base
- SmallEn
- Small
- MediumEn
- Medium
- LargeV1
- LargeV2
- LargeV3
- LargeV3Turbo (default)

## How It Works

### For File Input

1. The program downloads the specified Whisper model if not already present (stored in `%LOCALAPPDATA%/WhisperCLI/Models` on Windows)
2. FFmpeg is downloaded automatically if not already present
3. The input audio/video file is converted to the proper WAV format using FFmpeg
4. The audio is processed using the Whisper model
5. The transcription is saved in the selected output format in the same location as the input file

### For Folder Input

1. Pass `--folder <path>` to process every FFmpeg-detectable media file in that folder
2. Add `-r` or `--recursive` to include subfolders
3. Each output file is saved next to its source media file

### For Microphone Input

1. The program downloads the specified Whisper model if not already present
2. Audio is recorded from the selected microphone until the stop key is pressed
3. The recording is saved as a WAV file in your temp directory
4. The audio is processed using the Whisper model
5. The transcription is saved as plain text alongside the recording

## Dependencies

- [Whisper.net](https://github.com/sandrohanea/whisper.net)
- [Xabe.FFmpeg](https://github.com/tomaszzmuda/Xabe.FFmpeg)
- [NAudio](https://github.com/naudio/NAudio) (for microphone recording)
- [Serilog](https://serilog.net/)
- [CommandLineParser](https://github.com/commandlineparser/commandline)
- CUDA runtime support (optional for GPU acceleration)

## Builds and Releases

Pushes to `main` run dependency auditing, a Release build, subtitle tests, and a transcription check using the standalone executable. A successful build publishes the executable and its SHA-256 checksum to GitHub Releases. Pull requests run the same validation without publishing a release.

GitVersion 6.7.0 calculates the build version. The release policy starts at `1.0.0` and increments the patch version after the latest stable release tag. Rebuilding an already tagged commit uses its existing version. The same version is embedded in the executable and reported by `--version`.

## License

This project is licensed under the [MIT License](LICENSE).

## Acknowledgements

- [OpenAI Whisper](https://github.com/openai/whisper)
- [FFmpeg](https://ffmpeg.org/)
