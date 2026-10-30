namespace WhisperCLI
{
    internal static class AppPaths
    {
        private const string AppDirectoryName = "WhisperCLI";

        public static string RootDirectory
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    throw new InvalidOperationException("Local application data directory is not available.");
                }

                return Directory.CreateDirectory(Path.Combine(localAppData, AppDirectoryName)).FullName;
            }
        }

        public static string FFmpegDirectory => GetDirectory("FFmpeg");

        public static string ModelsDirectory => GetDirectory("Models");

        public static string ConversionsDirectory => GetDirectory("Conversions");

        public static string RecordingsDirectory => GetDirectory("Recordings");

        public static string TranscriptsDirectory => GetDirectory("Transcripts");

        public static string LockFilePath => Path.Combine(RootDirectory, "whisper.lock");

        private static string GetDirectory(string name)
        {
            return Directory.CreateDirectory(Path.Combine(RootDirectory, name)).FullName;
        }
    }
}
