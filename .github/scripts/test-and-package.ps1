param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$AssemblyVersion,
    [Parameter(Mandatory = $true)][string]$FileVersion
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$publishDirectory = Join-Path $repositoryRoot "artifacts/publish/win-x64"
$releaseDirectory = Join-Path $repositoryRoot "artifacts/release"
$versionProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$AssemblyVersion",
    "-p:FileVersion=$FileVersion",
    "-p:InformationalVersion=$Version",
    "-p:IncludeSourceRevisionInInformationalVersion=false"
)

Push-Location $repositoryRoot
try {
    dotnet restore Sources/WhisperCLI.sln -p:SelfContained=true --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Dependency restore or audit failed." }

    dotnet build Sources/WhisperCLI.sln -c Release --no-restore @versionProperties
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

    dotnet test Sources/WhisperCLI.sln -c Release --no-build --no-restore --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

    dotnet publish Sources/WhisperCLI.csproj --no-restore -p:PublishProfile=FolderProfile -o $publishDirectory @versionProperties
    if ($LASTEXITCODE -ne 0) { throw "Windows publication failed." }

    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $executable = Join-Path $releaseDirectory "WhisperCLI-win-x64.exe"
    Copy-Item -LiteralPath (Join-Path $publishDirectory "WhisperCLI.exe") -Destination $executable -Force

    $metadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    if ($metadata.ProductVersion -ne $Version -or $metadata.FileVersion -ne $FileVersion) {
        throw "Executable version metadata does not match the release version."
    }

    $versionOutput = (& $executable --version 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or -not $versionOutput.Contains($Version, [StringComparison]::Ordinal)) {
        throw "The standalone executable did not report the release version."
    }
    Write-Host $versionOutput

    & (Join-Path $PSScriptRoot "test-executable.ps1") -Executable $executable -OutputDirectory (Join-Path $repositoryRoot "artifacts/smoke")

    $checksum = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $releaseDirectory "SHA256SUMS") -Value "$checksum  WhisperCLI-win-x64.exe" -Encoding utf8NoBOM
}
finally {
    Pop-Location
}
