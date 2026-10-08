using Serilog;
using System.Diagnostics;
using System.Text;
using Whisper.net;
using Xabe.FFmpeg;

namespace WhisperCLI.Transcribers
{
    public class FileTranscriber(ILogger _logger)
    {
        private async Task CheckFfmpegAsync(CancellationToken token)
        {
            await FFmpegBootstrapper.EnsureAvailableAsync(_logger, token).ConfigureAwait(false);
        }

        private async Task<MemoryStream> ConvertToWaveStreamAsync(FileInfo inputFile)
        {
            string targetFile = Path.Combine(AppPaths.ConversionsDirectory, $"{Path.GetFileNameWithoutExtension(inputFile.Name)}-{Guid.NewGuid():N}.wav");
            bool isVideo = IsVideoFile(inputFile);
            IConversion conversion = isVideo
                ? await FFmpeg.Conversions.FromSnippet.ExtractAudio(inputFile.FullName, targetFile)
                : await FFmpeg.Conversions.FromSnippet.Convert(inputFile.FullName, targetFile);

            conversion.AddParameter("-ar 16000", ParameterPosition.PostInput);
            conversion.OnProgress += (sender, args) =>
            {
                _logger.Information("Converting media to wave: {argsPercent}%", args.Percent);
            };
            try
            {
                await conversion.Start();
                byte[] bytes = await File.ReadAllBytesAsync(targetFile);
                return new MemoryStream(bytes);
            }
            finally
            {
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
            }
        }

        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, Task<WhisperProcessor> processorTask, OutputFormat format, CancellationToken token)
        {
            await using WhisperProcessor processor = await processorTask.ConfigureAwait(false);
            return await TranscribeAudioAsync(inputFile, processor, format, token);
        }

        public async Task<FileInfo> TranscribeAudioAsync(FileInfo inputFile, WhisperProcessor processor, OutputFormat format, CancellationToken token)
        {
            await CheckFfmpegAsync(token);
            await using MemoryStream waves = await ConvertToWaveStreamAsync(inputFile);
            List<TranscriptSegment> segments = [];
            Stopwatch sw = Stopwatch.StartNew();
            _logger.Information("Starting transcription for {inputFile}", inputFile.Name);
            await foreach (SegmentData result in processor.ProcessAsync(waves, token))
            {
                segments.Add(new TranscriptSegment(result.Start, result.End, result.Text));
                _logger.Information("{lang}: {start}->{end}: {text}", result.Language,
                    result.Start.ToString(@"hh\:mm\:ss"), result.End.ToString(@"hh\:mm\:ss"), result.Text);
                if (token.IsCancellationRequested)
                {
                    _logger.Information("Cancellation requested - stopping recognition");
                    break;
                }
            }
            _logger.Information("Elapsed: {el}", sw.Elapsed.ToString(@"hh\:mm\:ss"));
            string outputFilePath = Path.ChangeExtension(inputFile.FullName, TranscriptFormatter.GetFileExtension(format));
            await File.WriteAllTextAsync(outputFilePath, TranscriptFormatter.Format(segments, format), Encoding.UTF8, token);
            _logger.Information("Transcription complete. Output saved to: {outputFilePath}", outputFilePath);
            return new FileInfo(outputFilePath);
        }

        private static bool IsVideoFile(FileInfo inputFile)
        {
            return inputFile.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   inputFile.Extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
        }
    }
}
