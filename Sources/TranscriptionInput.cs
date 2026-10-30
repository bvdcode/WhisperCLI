namespace WhisperCLI
{
    internal record TranscriptionInput(IReadOnlyList<FileInfo> Files, bool FolderMode);
}
