using Serilog.Core;
using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperCLI
{
    internal static class WhisperModelProvider
    {
        public static Task<FileInfo> GetWhisperModelPathAsync(GgmlType model, Logger logger, CancellationToken token)
        {
            string modelName = $"ggml-{model.ToString().ToLower()}.bin";
            return GetModelFileAsync(modelName,
                () => WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: token), logger, token);
        }

        public static async Task<WhisperVadProcessor> CreateVadAsync(float threshold, Logger logger, CancellationToken token)
        {
            FileInfo model = await GetModelFileAsync("ggml-silero-v6.2.0.bin",
                () => WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V6_2_0, token), logger, token);
            return WhisperProcessorFactory.CreateVad(model, threshold, logger);
        }

        private static async Task<FileInfo> GetModelFileAsync(string modelName, Func<Task<Stream>> download, Logger logger, CancellationToken token)
        {
            string filePath = Path.Combine(AppPaths.ModelsDirectory, modelName);
            string partialPath = filePath + ".tmp";

            // Clean up leftover partial download from a previous crashed/cancelled run.
            if (File.Exists(partialPath))
            {
                logger.Warning("Removing leftover partial download: {partialPath}", partialPath);
                File.Delete(partialPath);
            }

            FileInfo fileInfo = new(filePath);
            if (fileInfo.Exists)
            {
                logger.Information("Model already exists: {filePath}", fileInfo.FullName);
                return fileInfo;
            }

            logger.Information("Downloading model: {model}", modelName);
            await using (Stream modelStream = await download())
            await using (FileStream fileWriter = File.Create(partialPath))
            {
                await CopyWithProgressAsync(modelStream, fileWriter, logger, token);
            }
            File.Move(partialPath, filePath);
            fileInfo.Refresh();
            logger.Information("Model downloaded: {filePath}", fileInfo.FullName);
            return fileInfo;
        }

        private static async Task CopyWithProgressAsync(Stream source, Stream destination, Logger logger, CancellationToken token)
        {
            long? totalSize = null;
            try { totalSize = source.Length; }
            catch (NotSupportedException) { }

            byte[] buffer = new byte[81920];
            long totalBytes = 0;
            long lastLoggedBytes = 0;
            int lastLoggedPct = -5;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                totalBytes += read;

                if (totalSize.HasValue && totalSize.Value > 0)
                {
                    int pct = (int)(100 * totalBytes / totalSize.Value);
                    if (pct >= lastLoggedPct + 5)
                    {
                        logger.Information("Downloading: {pct}% ({mb:0.0} / {totalMb:0.0} MB)",
                            pct, totalBytes / 1048576.0, totalSize.Value / 1048576.0);
                        lastLoggedPct = pct;
                    }
                }
                else if (totalBytes - lastLoggedBytes >= 25L * 1024 * 1024)
                {
                    logger.Information("Downloading: {mb:0.0} MB", totalBytes / 1048576.0);
                    lastLoggedBytes = totalBytes;
                }
            }
            logger.Information("Download complete: {mb:0.0} MB", totalBytes / 1048576.0);
        }

        public static async Task<WhisperProcessor> CreateProcessorAsync(GgmlType model, FileInfo whisperModelInfo, Logger logger, string language, int beamSize, CancellationToken token)
        {
            logger.Information("Creating WhisperProcessor with language: {language}, model: {model}...", language, model);

            try
            {
                return await Task.Run(() => WhisperProcessorFactory.Create(whisperModelInfo, language, logger, model, beamSize), token).ConfigureAwait(false);
            }
            catch (WhisperModelLoadException ex)
            {
                logger.Warning(ex, "Model load failed — file may be corrupt. Removing and re-downloading: {filePath}", whisperModelInfo.FullName);
                File.Delete(whisperModelInfo.FullName);
                whisperModelInfo = await GetWhisperModelPathAsync(model, logger, token).ConfigureAwait(false);
                return await Task.Run(() => WhisperProcessorFactory.Create(whisperModelInfo, language, logger, model, beamSize), token).ConfigureAwait(false);
            }
        }
    }
}
