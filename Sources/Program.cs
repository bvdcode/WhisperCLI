using Serilog;
using System.Text;
using Whisper.net;
using Serilog.Core;
using Serilog.Events;
using Xabe.FFmpeg;
using Whisper.net.Ggml;
using System.Diagnostics;
using Whisper.net.Logger;
using WhisperCLI.Transcribers;

namespace WhisperCLI
{
    public class Program
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

        public static async Task Main(string[] args)
        {
            var parserResult = CommandLine.Parser.Default.ParseArguments<AppOptions>(args);
            if (parserResult is not CommandLine.Parsed<AppOptions> parsedOptions)
            {
                return;
            }

            AppOptions options = parsedOptions.Value;
            ArgumentOutOfRangeException.ThrowIfNegative(options.DelaySeconds, "Delay seconds must be non-negative.");
            Console.OutputEncoding = Encoding.UTF8;
            CancellationTokenSource cts = new();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true; // Prevent the process from terminating immediately
                cts.Cancel(); // Signal cancellation
            };
            Logger logger = new LoggerConfiguration()
                .MinimumLevel.Is(options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            LogProvider.AddLogger((level, text) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    logger.Debug("[Whisper] [{level}] {text}", level.ToString().ToUpperInvariant(), text.Trim());
                }
            });
            if (options.UseLockfile && CheckLockfile(logger))
            {
                await DelayBeforeExitAsync(options, cts.Token);
                return;
            }

            List<FileInfo> inputFiles = [];
            bool folderMode = !string.IsNullOrWhiteSpace(options.FolderPath);
            if (folderMode)
            {
                if (!string.IsNullOrWhiteSpace(options.InputFilePath))
                {
                    logger.Error("Specify either an input file or --folder, not both.");
                    await DelayBeforeExitAsync(options, cts.Token);
                    return;
                }

                DirectoryInfo folder = new(options.FolderPath);
                if (!folder.Exists)
                {
                    logger.Error("Input folder does not exist: {folderPath}", options.FolderPath);
                    await DelayBeforeExitAsync(options, cts.Token);
                    return;
                }

                logger.Information("Scanning folder: {folderPath}", folder.FullName);
                inputFiles = await GetMediaFilesAsync(folder, options.Recursive, logger, cts.Token);
                if (inputFiles.Count == 0)
                {
                    logger.Warning("No media files found in folder: {folderPath}", folder.FullName);
                    await DelayBeforeExitAsync(options, cts.Token);
                    return;
                }

                logger.Information("Found {count} media file(s) in {folderPath}.", inputFiles.Count, folder.FullName);
            }
            else if (!string.IsNullOrWhiteSpace(options.InputFilePath))
            {
                DirectoryInfo inputDirectory = new(options.InputFilePath);
                if (inputDirectory.Exists)
                {
                    logger.Information("Scanning folder: {folderPath}", inputDirectory.FullName);
                    inputFiles = await GetMediaFilesAsync(inputDirectory, options.Recursive, logger, cts.Token);
                    if (inputFiles.Count == 0)
                    {
                        logger.Warning("No media files found in folder: {folderPath}", inputDirectory.FullName);
                        await DelayBeforeExitAsync(options, cts.Token);
                        return;
                    }

                    logger.Information("Found {count} media file(s) in {folderPath}.", inputFiles.Count, inputDirectory.FullName);
                }
                else
                {
                    FileInfo inputFile = new(options.InputFilePath);
                    if (!inputFile.Exists)
                    {
                        logger.Error("Input file or folder does not exist: {inputFilePath}", options.InputFilePath);
                        await DelayBeforeExitAsync(options, cts.Token);
                        return;
                    }

                    inputFiles.Add(inputFile);
                }
            }

            FileInfo whisperModelInfo = await GetWhisperModelPathAsync(options.Model, logger, cts.Token);
            var processorTask = CreateProcessorAsync(options.Model, whisperModelInfo, logger, options.Language, cts.Token);
            List<FileInfo> results = [];
            try
            {
                if (inputFiles.Count == 0)
                {
                    logger.Information("Press {stopKey} to stop recording.", options.StopKey);
                    FileInfo result = await new MicrophoneTranscriber(logger, options.MicrophoneIndex)
                        .TranscribeAudioAsync(processorTask, options.SaveTranscript, OutputFormat.Txt, () => CheckCancellation(options.StopKey), cts.Token);
                    results.Add(result);
                }
                else
                {
                    results.AddRange(await TranscribeFilesAsync(inputFiles, processorTask, options.Format, logger, cts.Token));
                }
                if (results.Count == 1 && options.OpenTextFile)
                {
                    OpenFile(results[0]);
                }
                else if (results.Count > 1 && options.OpenTextFile)
                {
                    logger.Information("Skipping --open-results because multiple output files were generated.");
                }

                if (results.Count == 1 && options.CopyToClipboard)
                {
                    try
                    {
                        string text = File.ReadAllText(results[0].FullName, Encoding.UTF8);
                        TextCopy.ClipboardService.SetText(text);
                        logger.Information("Transcription result copied to clipboard.");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to copy transcription result to clipboard.");
                    }
                }
                else if (results.Count > 1 && options.CopyToClipboard)
                {
                    logger.Information("Skipping clipboard copy because multiple output files were generated.");
                }

                await DelayBeforeExitAsync(options, cts.Token);
            }
            catch (TaskCanceledException)
            {
                logger.Information("Operation canceled by user.");
            }
            finally
            {
                string lockFilePath = GetLockFileLocation();
                File.Delete(lockFilePath);
            }
        }

        private static async Task DelayBeforeExitAsync(AppOptions options, CancellationToken token)
        {
            if (options.DelaySeconds > 0)
            {
                await Task.Delay(options.DelaySeconds * 1000, token);
            }
        }

        private static async Task<List<FileInfo>> GetMediaFilesAsync(DirectoryInfo folder, bool recursive, Logger logger, CancellationToken token)
        {
            await CheckFfmpegAsync(logger, token);
            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            List<FileInfo> mediaFiles = [];
            foreach (FileInfo file in folder.EnumerateFiles("*", searchOption).OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (!MediaExtensions.Contains(file.Extension))
                {
                    continue;
                }

                try
                {
                    var mediaInfo = await FFmpeg.GetMediaInfo(file.FullName, token);
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

        private static async Task CheckFfmpegAsync(Logger logger, CancellationToken token)
        {
            await FFmpegBootstrapper.EnsureAvailableAsync(logger, token).ConfigureAwait(false);
        }

        private static async Task<List<FileInfo>> TranscribeFilesAsync(
            IReadOnlyList<FileInfo> inputFiles,
            Task<WhisperProcessor> processorTask,
            OutputFormat format,
            Logger logger,
            CancellationToken token)
        {
            List<FileInfo> results = [];
            using var processor = await processorTask.ConfigureAwait(false);
            FileTranscriber transcriber = new(logger);
            for (int i = 0; i < inputFiles.Count; i++)
            {
                FileInfo inputFile = inputFiles[i];
                logger.Information("Processing file {current}/{total}: {inputFile}", i + 1, inputFiles.Count, inputFile.FullName);
                FileInfo result = await transcriber.TranscribeAudioAsync(inputFile, processor, format, token).ConfigureAwait(false);
                results.Add(result);
            }

            return results;
        }

        private static string GetLockFileLocation()
        {
            return AppPaths.LockFilePath;
        }

        private static bool CheckLockfile(Logger logger)
        {
            string lockFilePath = GetLockFileLocation();
            if (File.Exists(lockFilePath))
            {
                logger.Warning("Lock file exists. WhisperCLI may already be running.");
                return true;
            }
            try
            {
                using (File.Create(lockFilePath)) { }
                logger.Debug("Lock file created: {lockFilePath}", lockFilePath);
                return false;
            }
            catch (IOException ex)
            {
                logger.Error(ex, "Failed to create lock file: {lockFilePath}", lockFilePath);
                return true;
            }
            finally
            {
                // Ensure the lock file is deleted on exit
                AppDomain.CurrentDomain.ProcessExit += (s, e) => File.Delete(lockFilePath);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => File.Delete(lockFilePath);
                Console.CancelKeyPress += (s, e) => File.Delete(lockFilePath);
            }
        }

        private static bool CheckCancellation(ConsoleKey stopKey)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKey key = Console.ReadKey(true).Key;
                return key == stopKey;
            }
            return false;
        }

        private static void OpenFile(FileInfo fileInfo)
        {

            if (fileInfo.Exists)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = fileInfo.FullName,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Failed to open file: {filePath}", fileInfo.FullName);
                }
            }
            else
            {
                Log.Logger.Error("File does not exist: {filePath}", fileInfo.FullName);
            }
        }

        private static async Task<FileInfo> GetWhisperModelPathAsync(GgmlType model, Logger logger, CancellationToken token)
        {
            string modelName = $"ggml-{model.ToString().ToLower()}.bin";
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

            logger.Information("Downloading model: {model}", model);
            using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: token))
            using (var fileWriter = File.Create(partialPath))
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

        private static readonly Dictionary<string, string> LanguagePrompts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = "This is a speech transcript. Write natural text with sentence punctuation. Это транскрипция речи. Пишите обычным текстом с точками, запятыми и другими знаками препинания.",
            ["ar"] = "هذا نص لخطاب مباشر. اكتب نصاً عادياً مع النقاط والفواصل وعلامات الترقيم الأخرى.",
            ["bg"] = "Това е транскрипция на жива реч. Пишете нормален текст с точки, запетаи и други препинателни знаци.",
            ["cs"] = "Toto je přepis živé řeči. Pište běžným textem s tečkami, čárkami a dalšími interpunkčními znaménky.",
            ["da"] = "Dette er en transskription af live tale. Skriv normal tekst med punktummer, kommaer og andre tegnsætningstegn.",
            ["de"] = "Dies ist eine Transkription von Live-Sprache. Schreibe normalen Text mit Punkten, Kommas und anderen Satzzeichen.",
            ["el"] = "Αυτή είναι μια μεταγραφή ζωντανής ομιλίας. Γράψτε κανονικό κείμενο με τελείες, κόμματα και άλλα σημεία στίξης.",
            ["en"] = "This is a transcription of live speech. Write normal text with periods, commas, and other punctuation marks.",
            ["es"] = "Esta es una transcripción de habla en vivo. Escribe texto normal con puntos, comas y otros signos de puntuación.",
            ["fi"] = "Tämä on suoran puheen litterointi. Kirjoita tavallista tekstiä pisteillä, pilkuilla ja muilla välimerkeillä.",
            ["fr"] = "Ceci est une transcription de parole en direct. Écrivez en texte normal avec des points, des virgules et d'autres signes de ponctuation.",
            ["he"] = "זוהי תמלול של דיבור חי. כתבו טקסט רגיל עם נקודות, פסיקים וסימני פיסוק אחרים.",
            ["hu"] = "Ez élő beszéd átirata. Írjon normál szöveget pontokkal, vesszőkkel és egyéb írásjelekkel.",
            ["it"] = "Questa è una trascrizione di parlato dal vivo. Scrivi testo normale con punti, virgole e altri segni di punteggiatura.",
            ["ja"] = "これは生の音声の文字起こしです。句読点を使って通常の文章として書いてください。",
            ["ko"] = "이것은 실시간 음성의 전사입니다. 마침표, 쉼표 및 기타 구두점을 사용하여 일반 텍스트로 작성하세요.",
            ["nl"] = "Dit is een transcriptie van live spraak. Schrijf normale tekst met punten, komma's en andere leestekens.",
            ["no"] = "Dette er en transkripsjon av tale. Skriv normal tekst med punktum, komma og andre tegnsettingstegn.",
            ["pl"] = "To jest transkrypcja mowy na żywo. Pisz zwykłym tekstem z kropkami, przecinkami i innymi znakami interpunkcyjnymi.",
            ["pt"] = "Esta é uma transcrição de fala ao vivo. Escreva texto normal com pontos, vírgulas e outros sinais de pontuação.",
            ["ro"] = "Aceasta este o transcriere a vorbirii live. Scrieți text normal cu puncte, virgule și alte semne de punctuație.",
            ["ru"] = "Это транскрипция живой речи. Пишите обычным текстом с точками, запятыми и другими знаками препинания.",
            ["sv"] = "Detta är en transkription av live-tal. Skriv normal text med punkter, kommatecken och andra skiljetecken.",
            ["tr"] = "Bu, canlı konuşmanın transkripsiyonudur. Noktalar, virgüller ve diğer noktalama işaretleriyle normal metin yazın.",
            ["uk"] = "Це транскрипція живого мовлення. Пишіть звичайним текстом з крапками, комами та іншими розділовими знаками.",
            ["zh"] = "这是现场语音的转录。请用正常文本书写，使用句号、逗号和其他标点符号。",
        };

        private static async Task<WhisperProcessor> CreateProcessorAsync(GgmlType model, FileInfo whisperModelInfo, Logger logger, string language, CancellationToken token)
        {
            logger.Information("Creating WhisperProcessor with language: {language}, model: {model}...", language, model);
            LanguagePrompts.TryGetValue(language, out string? prompt);

            try
            {
                return await Task.Run(() => BuildProcessor(whisperModelInfo, language, prompt, logger, model), token).ConfigureAwait(false);
            }
            catch (WhisperModelLoadException ex)
            {
                logger.Warning(ex, "Model load failed — file may be corrupt. Removing and re-downloading: {filePath}", whisperModelInfo.FullName);
                File.Delete(whisperModelInfo.FullName);
                whisperModelInfo = await GetWhisperModelPathAsync(model, logger, token).ConfigureAwait(false);
                return await Task.Run(() => BuildProcessor(whisperModelInfo, language, prompt, logger, model), token).ConfigureAwait(false);
            }
        }

        private static WhisperProcessor BuildProcessor(FileInfo whisperModelInfo, string language, string? prompt, Logger logger, GgmlType model)
        {
            WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelInfo.FullName);
            logger.Information("WhisperProcessor loaded in background: {model}", model);
            var builder = whisperFactory
                .CreateBuilder()
                .WithLanguage(language)
                .WithTemperature(0f)
                .WithTemperatureInc(0f)
                .WithMaxSegmentLength(160);
            if (!string.IsNullOrEmpty(prompt))
            {
                builder = builder.WithPrompt(prompt);
            }
            return builder.Build();
        }
    }
}
