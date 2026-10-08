using Serilog.Core;
using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperCLI
{
    public static class WhisperProcessorFactory
    {
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

        public static WhisperProcessor Create(FileInfo whisperModelInfo, string language, Logger logger, GgmlType model)
        {
            LanguagePrompts.TryGetValue(language, out string? prompt);
            WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelInfo.FullName);
            logger.Information("WhisperProcessor loaded in background: {model}", model);
            WhisperProcessorBuilder builder = whisperFactory
                .CreateBuilder()
                .WithLanguage(language)
                .WithTemperature(0f)
                .WithMaxSegmentLength(160);
            if (!string.IsNullOrEmpty(prompt))
            {
                builder = builder.WithPrompt(prompt);
            }
            return builder.Build();
        }
    }
}
