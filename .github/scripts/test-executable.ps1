param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$Executable = [System.IO.Path]::GetFullPath($Executable)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$inputFile = Join-Path $OutputDirectory "silence.wav"
$subtitleFile = [System.IO.Path]::ChangeExtension($inputFile, ".srt")
[System.IO.File]::Delete($subtitleFile)

$sampleRate = 16000
$audioBytes = [byte[]]::new($sampleRate * 2)
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($inputFile))
try {
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
    $writer.Write([int](36 + $audioBytes.Length))
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
    $writer.Write([int]16)
    $writer.Write([short]1)
    $writer.Write([short]1)
    $writer.Write([int]$sampleRate)
    $writer.Write([int]($sampleRate * 2))
    $writer.Write([short]2)
    $writer.Write([short]16)
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
    $writer.Write([int]$audioBytes.Length)
    $writer.Write($audioBytes)
}
finally {
    $writer.Dispose()
}

$transcriptionOutput = (& $Executable -m TinyEn -l en -d 0 $inputFile 2>&1) -join [Environment]::NewLine
$transcriptionExitCode = $LASTEXITCODE
Set-Content -LiteralPath (Join-Path $OutputDirectory "transcription.log") -Value $transcriptionOutput -Encoding utf8NoBOM
if ($transcriptionExitCode -ne 0 -or -not (Test-Path -LiteralPath $subtitleFile) -or
    -not $transcriptionOutput.Contains("Transcription complete", [StringComparison]::Ordinal)) {
    Write-Host $transcriptionOutput
    throw "The standalone executable failed to load its native runtime and transcribe audio."
}
Write-Host "Standalone native runtime and file transcription passed."

$nestedDirectory = Join-Path $OutputDirectory 'nested'
New-Item -ItemType Directory -Path $nestedDirectory -Force | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $nestedDirectory 'complete.mp3'), [byte[]]::new(0))
$completedOutput = Join-Path $nestedDirectory 'complete.srt'
[System.IO.File]::WriteAllText($completedOutput, 'Existing transcript')
foreach ($arguments in @(@('--folder', $OutputDirectory), @($OutputDirectory))) {
    $scanOutput = (& $Executable @arguments -r -m TinyEn -d 0 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or -not $scanOutput.Contains('transcript already exists', [StringComparison]::Ordinal) -or
        $scanOutput.Contains('Checking FFmpeg', [StringComparison]::Ordinal) -or
        $scanOutput.Contains('Creating WhisperProcessor', [StringComparison]::Ordinal) -or
        [System.IO.File]::ReadAllText($completedOutput) -cne 'Existing transcript') {
        Write-Host $scanOutput
        throw 'A folder with completed transcripts must finish without probing or loading the model.'
    }
}
Write-Host 'Completed recursive folder inputs were skipped before probing and model loading.'

$queueDirectory = Join-Path $OutputDirectory 'queue'
New-Item -ItemType Directory -Path $queueDirectory -Force | Out-Null
[System.IO.File]::Delete((Join-Path $queueDirectory 'recording.srt'))
Copy-Item -LiteralPath $inputFile -Destination (Join-Path $queueDirectory 'recording.wav') -Force
Copy-Item -LiteralPath $inputFile -Destination (Join-Path $queueDirectory 'recording.mp3') -Force
$queueOutput = (& $Executable --folder $queueDirectory -r -m TinyEn -l en -d 0 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0 -or ([regex]::Matches($queueOutput, 'Starting transcription for')).Count -ne 1 -or
    -not $queueOutput.Contains('transcript already exists', [StringComparison]::Ordinal)) {
    Write-Host $queueOutput
    throw 'An output created earlier in the queue must not be transcribed again.'
}
Write-Host 'Folder processing rechecked existing transcripts before each queued file.'
