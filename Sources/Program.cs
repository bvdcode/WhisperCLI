using Serilog;
using System.Text;
using Whisper.net;
using Serilog.Core;
using Serilog.Events;
using Whisper.net.Ggml;
using System.Diagnostics;
using Whisper.net.Logger;
using WhisperCLI.Transcribers;

namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            AppOptions options = CommandLine.Parser.Default.ParseArguments<AppOptions>(args).Value;
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
                await Task.Delay(options.DelaySeconds * 1000, cts.Token);
                return;
            }
            FileInfo whisperModelInfo = await GetWhisperModelPathAsync(options.Model, logger, cts.Token);
            var processorTask = CreateProcessorAsync(options.Model, whisperModelInfo, logger, options.Language);
            FileInfo result;
            try
            {
                if (string.IsNullOrWhiteSpace(options.InputFilePath))
                {
                    logger.Information("Press {stopKey} to stop recording.", options.StopKey);
                    result = await new MicrophoneTranscriber(logger, options.MicrophoneIndex)
                        .TranscribeAudioAsync(processorTask, options.SaveTranscript, () => CheckCancellation(options.StopKey), cts.Token);
                }
                else
                {
                    FileInfo inputFile = new(options.InputFilePath);
                    if (!inputFile.Exists)
                    {
                        logger.Error("Input file does not exist: {inputFilePath}", options.InputFilePath);
                        return;
                    }
                    result = await new FileTranscriber(logger)
                        .TranscribeAudioAsync(inputFile, processorTask, cts.Token);
                }
                if (options.OpenTextFile)
                {
                    OpenFile(result);
                }
                if (options.CopyToClipboard)
                {
                    try
                    {
                        string text = File.ReadAllText(result.FullName, Encoding.UTF8);
                        TextCopy.ClipboardService.SetText(text);
                        logger.Information("Transcription result copied to clipboard.");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to copy transcription result to clipboard.");
                    }
                }
                await Task.Delay(options.DelaySeconds * 1000, cts.Token);
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

        private static string GetLockFileLocation()
        {
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI");
            var di = Directory.CreateDirectory(workingDirectory);
            return Path.Combine(di.FullName, "whisper.lock");
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
            string tempPath = Path.GetTempPath();
            string workingDirectory = Path.Combine(tempPath, "WhisperCLI", "Models");
            var di = Directory.CreateDirectory(workingDirectory);

            string filePath = Path.Combine(di.FullName, modelName);
            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists)
            {
                using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: token);
                logger.Information("Downloading model: {_ggmlType}", model);
                using var fileWriter = fileInfo.Create();
                await modelStream.CopyToAsync(fileWriter, token);
                logger.Information("Model downloaded: {filePath}", fileInfo.FullName);
            }
            else
            {
                logger.Information("Model already exists: {filePath}", fileInfo.FullName);
            }
            return fileInfo;
        }

        private static readonly Dictionary<string, string> LanguagePrompts = new(StringComparer.OrdinalIgnoreCase)
        {
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

        private static Task<WhisperProcessor> CreateProcessorAsync(GgmlType model, FileInfo whisperModelInfo, Logger logger, string language)
        {
            logger.Information("Creating WhisperProcessor with language: {language}, model: {model}...", language, model);
            LanguagePrompts.TryGetValue(language, out string? prompt);
            try
            {
                return Task.Run(() =>
                {
                    WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelInfo.FullName);
                    logger.Information("WhisperProcessor loaded in background: {model}", model);
                    var builder = whisperFactory
                        .CreateBuilder()
                        .WithLanguage(language)
                        .WithTemperature(0.2f)
                        .WithMaxSegmentLength(80);
                    if (!string.IsNullOrEmpty(prompt))
                    {
                        builder = builder.WithPrompt(prompt);
                    }
                    return builder.Build();
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error occurred while creating WhisperProcessor");
                Environment.Exit(-1);
                throw;
            }
        }
    }
}
