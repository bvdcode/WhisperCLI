namespace WhisperCLI.Transcribers.Robust;

public sealed class SpeechRegion
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
}

public sealed class AudioChunk
{
    public string Id { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public bool ExpectedSpeech { get; set; }

    public TimeSpan Duration => End - Start;
}

public sealed class TranscriptSegment
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
}

public sealed class QualityAssessment
{
    public double Score { get; set; }
    public bool Suspicious { get; set; }
    public int WordCount { get; set; }
    public int MaxConsecutiveDuplicateSegments { get; set; }
    public int LongestRepeatedPhraseWords { get; set; }
    public int LongestRepeatedPhraseCount { get; set; }
    public double RepeatedTokenFraction { get; set; }
    public double UniqueBigramRatio { get; set; }
    public double CharactersPerSecond { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class AttemptDiagnostic
{
    public string Model { get; set; } = string.Empty;
    public string Strategy { get; set; } = string.Empty;
    public TimeSpan AudioStart { get; set; }
    public TimeSpan AudioEnd { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool AbortedForLiveLoop { get; set; }
    public string? Error { get; set; }
    public QualityAssessment Quality { get; set; } = new();
}

public sealed class ChunkTranscriptionResult
{
    public AudioChunk Chunk { get; set; } = new();
    public string SelectedModel { get; set; } = string.Empty;
    public string SelectedStrategy { get; set; } = string.Empty;
    public bool NeedsReview { get; set; }
    public QualityAssessment Quality { get; set; } = new();
    public List<TranscriptSegment> Segments { get; set; } = [];
    public List<AttemptDiagnostic> Attempts { get; set; } = [];
    public List<ChunkTranscriptionResult> SubChunks { get; set; } = [];
}

public sealed class TranscriptionCheckpoint
{
    public string SchemaVersion { get; set; } = "9";
    public string InputPath { get; set; } = string.Empty;
    public long InputLength { get; set; }
    public DateTime InputLastWriteUtc { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<ChunkTranscriptionResult> CompletedChunks { get; set; } = [];
}

public sealed class TranscriptionRunReport
{
    public string SchemaVersion { get; set; } = "9";
    public string InputPath { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public string PrimaryModel { get; set; } = string.Empty;
    public List<string> FallbackModels { get; set; } = [];
    public string RequestedLanguage { get; set; } = string.Empty;
    public string? LockedDetectedLanguage { get; set; }
    public bool UsedVad { get; set; }
    public int VadSpeechRegionCount { get; set; }
    public TimeSpan AudioDuration { get; set; }
    public bool NeedsReview { get; set; }
    public List<ChunkTranscriptionResult> Chunks { get; set; } = [];
}
