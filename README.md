# WhisperCLI

WhisperCLI is a command-line tool for transcribing audio and video files using OpenAI's Whisper speech recognition models via the Whisper.net library.

## Features

- Transcribe audio and video files to text
- Support for various audio and video formats (mp3, mp4, mkv, avi, etc.)
- Automatic downloading of Whisper models
- Automatic downloading of FFmpeg
- Support for different Whisper model sizes (default: LargeV3Turbo)
- Cross-platform functionality (Windows and Unix)
- Progress reporting during conversion and transcription

## Requirements

- .NET 9.0

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

```
WhisperCLI <inputFilePath> [modelType]
```

### Arguments

- `inputFilePath`: Path to the audio or video file to transcribe
- `modelType` (optional): The Whisper model to use (default: LargeV3Turbo)

### Examples

```
WhisperCLI audio.mp3
WhisperCLI video.mp4 LargeV3Turbo
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

1. The program downloads the specified Whisper model if not already present (stored in your temp directory)
2. FFmpeg is downloaded automatically if not already present
3. The input audio/video file is converted to the proper WAV format using FFmpeg
4. The audio is processed using the Whisper model
5. The transcription is saved as a text file in the same location as the input file

## Dependencies

- [Whisper.net](https://github.com/sandrohanea/whisper.net)
- [Xabe.FFmpeg](https://github.com/tomaszzmuda/Xabe.FFmpeg)
- [Serilog](https://serilog.net/)
- CUDA runtime support (optional for GPU acceleration)

## Acknowledgements

- [OpenAI Whisper](https://github.com/openai/whisper)
- [FFmpeg](https://ffmpeg.org/)
