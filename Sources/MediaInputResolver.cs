using Serilog;
using Xabe.FFmpeg;

namespace WhisperCLI
{
    internal static class MediaInputResolver
    {
        public static async Task<TranscriptionInput?> ResolveAsync(AppOptions options, ILogger logger, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(options.FolderPath))
            {
                if (!string.IsNullOrWhiteSpace(options.InputFilePath))
                {
                    logger.Error("Specify either an input file or --folder, not both.");
                    return null;
                }

                DirectoryInfo folder = new(options.FolderPath);
                if (!folder.Exists)
                {
                    logger.Error("Input folder does not exist: {folderPath}", options.FolderPath);
                    return null;
                }

                return await ScanFolderAsync(folder, options, logger, token);
            }

            if (string.IsNullOrWhiteSpace(options.InputFilePath))
            {
                return new TranscriptionInput([], false);
            }

            DirectoryInfo inputDirectory = new(options.InputFilePath);
            if (inputDirectory.Exists)
            {
                return await ScanFolderAsync(inputDirectory, options, logger, token);
            }

            FileInfo inputFile = new(options.InputFilePath);
            if (!inputFile.Exists)
            {
                logger.Error("Input file or folder does not exist: {inputFilePath}", options.InputFilePath);
                return null;
            }

            return new TranscriptionInput([inputFile], false);
        }

        private static async Task<TranscriptionInput?> ScanFolderAsync(DirectoryInfo folder, AppOptions options, ILogger logger, CancellationToken token)
        {
            logger.Information("Scanning folder: {folderPath}", folder.FullName);
            List<FileInfo> files = await GetMediaFilesAsync(folder, options.Recursive, options.Format, logger, token);
            if (files.Count == 0)
            {
                logger.Warning("No media files require transcription in folder: {folderPath}", folder.FullName);
                return null;
            }

            logger.Information("Found {count} media file(s) in {folderPath}.", files.Count, folder.FullName);
            return new TranscriptionInput(files, true);
        }

        private static async Task<List<FileInfo>> GetMediaFilesAsync(DirectoryInfo folder, bool recursive, OutputFormat format, ILogger logger, CancellationToken token)
        {
            List<FileInfo> mediaFiles = [];
            foreach (FileInfo file in MediaFileScanner.EnumeratePending(folder, recursive, format, logger, token))
            {
                await FFmpegBootstrapper.EnsureAvailableAsync(logger, token).ConfigureAwait(false);
                try
                {
                    IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(file.FullName, token);
                    if (mediaInfo.AudioStreams.Any() || mediaInfo.VideoStreams.Any())
                    {
                        mediaFiles.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Skipping non-media file: {filePath}", file.FullName);
                }
            }

            return mediaFiles;
        }
    }
}
