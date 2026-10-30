using Serilog;

namespace WhisperCLI
{
    internal static class MediaFileScanner
    {
        private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".3g2",
            ".3gp",
            ".aac",
            ".ac3",
            ".aif",
            ".aiff",
            ".amr",
            ".ape",
            ".asf",
            ".au",
            ".avi",
            ".caf",
            ".dts",
            ".dv",
            ".f4v",
            ".flac",
            ".flv",
            ".m2ts",
            ".m4a",
            ".m4b",
            ".m4v",
            ".mka",
            ".mkv",
            ".mov",
            ".mp2",
            ".mp3",
            ".mp4",
            ".mpa",
            ".mpeg",
            ".mpg",
            ".mts",
            ".oga",
            ".ogg",
            ".ogm",
            ".ogv",
            ".opus",
            ".ra",
            ".rm",
            ".rmvb",
            ".ts",
            ".vob",
            ".wav",
            ".webm",
            ".wma",
            ".wmv"
        };

        public static IEnumerable<FileInfo> EnumeratePending(
            DirectoryInfo folder, bool recursive, OutputFormat format, ILogger logger, CancellationToken token)
        {
            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (FileInfo file in folder.EnumerateFiles("*", searchOption).OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (!MediaExtensions.Contains(file.Extension))
                {
                    continue;
                }

                string outputPath = TranscriptFormatter.GetOutputPath(file, format);
                if (File.Exists(outputPath))
                {
                    logger.Information("Skipping {inputFile}: transcript already exists at {outputPath}.", file.FullName, outputPath);
                    continue;
                }

                yield return file;
            }
        }
    }
}
