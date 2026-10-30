$ErrorActionPreference = "Stop"

dotnet tool restore

$gitVersionJson = (& dotnet gitversion /output json) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    throw "GitVersion exited with code $LASTEXITCODE."
}

$gitVersion = $gitVersionJson | ConvertFrom-Json
$version = [string]$gitVersion.MajorMinorPatch
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "GitVersion did not return MajorMinorPatch."
}

$assemblyVersion = "$version.0"
$fileVersion = "$version.0"

if ($env:GITHUB_OUTPUT) {
    "SemVer=$version" >> $env:GITHUB_OUTPUT
    "semVer=$version" >> $env:GITHUB_OUTPUT
    "MajorMinorPatch=$version" >> $env:GITHUB_OUTPUT
    "AssemblyVersion=$assemblyVersion" >> $env:GITHUB_OUTPUT
    "FileVersion=$fileVersion" >> $env:GITHUB_OUTPUT
    "GitVersionSemVer=$($gitVersion.SemVer)" >> $env:GITHUB_OUTPUT
    "InformationalVersion=$($gitVersion.InformationalVersion)" >> $env:GITHUB_OUTPUT
    "Sha=$($gitVersion.Sha)" >> $env:GITHUB_OUTPUT
}

Write-Host "GitVersion SemVer: $($gitVersion.SemVer)"
Write-Host "GitVersion MajorMinorPatch: $($gitVersion.MajorMinorPatch)"
Write-Host "Release SemVer: $version"
Write-Host "Release AssemblyVersion: $assemblyVersion"
Write-Host "Release FileVersion: $fileVersion"
Write-Host "GitVersion InformationalVersion: $($gitVersion.InformationalVersion)"
