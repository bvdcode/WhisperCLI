# WhisperCLI

WhisperCLI is a command-line tool for transcribing audio from files or microphone input using OpenAI's Whisper speech recognition models via the Whisper.net library.

## Features

- Transcribe audio and video files to text
- Record and transcribe audio directly from microphone
- Support for various audio and video formats (mp3, mp4, mkv, avi, etc.)
- Automatic downloading of Whisper models
- Automatic downloading of FFmpeg
- Support for different Whisper model sizes (default: LargeV3Turbo)
- Cross-platform functionality (Windows and Unix)
- Progress reporting during conversion and transcription

## Requirements

- .NET 9.0

### If running on Linux

- xsel
- aplay, amixer, arecord (refer to https://github.com/mobiletechtracker/NetCoreAudio)

## Installation

### Using Published Release

Download the latest release from the releases page and extract it to your preferred location.

### Building from Source

1. Clone the repository
2. Navigate to the Sources directory
3. Build the project:
   ```
   dotnet build
   ```
4. Publish the project (optional):
   ```
   dotnet publish -c Release
   ```

## Usage

### Basic Usage

```
WhisperCLI [options] [inputFilePath]
```

### Command Line Options

- `-m, --model`: Model to use for transcription (default: LargeV3Turbo)
- `-i, --microphone-index`: Index of microphone to use for recording (default: 0)
- `-s, --stop-key`: Key to stop recording when using microphone input (default: Spacebar)
- `inputFilePath`: Path to the audio or video file to transcribe (if omitted, uses microphone input)

### Examples

```
# Transcribe an audio file
WhisperCLI input.mp3

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

1. The program downloads the specified Whisper model if not already present (stored in your temp directory)
2. FFmpeg is downloaded automatically if not already present
3. The input audio/video file is converted to the proper WAV format using FFmpeg
4. The audio is processed using the Whisper model
5. The transcription is saved as a text file in the same location as the input file

### For Microphone Input

1. The program downloads the specified Whisper model if not already present
2. Audio is recorded from the selected microphone until the stop key is pressed
3. The recording is saved as a WAV file in your temp directory
4. The audio is processed using the Whisper model
5. The transcription is saved alongside the recording and opened automatically

## Dependencies

- [Whisper.net](https://github.com/sandrohanea/whisper.net)
- [Xabe.FFmpeg](https://github.com/tomaszzmuda/Xabe.FFmpeg)
- [NAudio](https://github.com/naudio/NAudio) (for microphone recording)
- [Serilog](https://serilog.net/)
- [CommandLineParser](https://github.com/commandlineparser/commandline)
- CUDA runtime support (optional for GPU acceleration)

## License

This project is licensed under the [MIT License](LICENSE).

## Acknowledgements

- [OpenAI Whisper](https://github.com/openai/whisper)
- [FFmpeg](https://ffmpeg.org/)
