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
