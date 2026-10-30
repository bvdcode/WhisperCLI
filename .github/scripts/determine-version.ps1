$ErrorActionPreference = "Stop"

function Read-ConfiguredBaseVersion {
    $gitVersionConfig = Get-Content -LiteralPath "GitVersion.yml" -Raw
    $match = [regex]::Match($gitVersionConfig, "(?m)^next-version:\s*(?<version>\d+\.\d+\.\d+)\s*$")
    if (-not $match.Success) {
        throw "GitVersion.yml does not define a numeric next-version."
    }

    return $match.Groups["version"].Value
}

function Convert-ToVersionParts {
    param([string]$Version)

    $match = [regex]::Match($Version.Trim(), "^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$")
    if (-not $match.Success) {
        return $null
    }

    return [pscustomobject]@{
        Major = [int]$match.Groups["major"].Value
        Minor = [int]$match.Groups["minor"].Value
        Patch = [int]$match.Groups["patch"].Value
        Text = "$($match.Groups["major"].Value).$($match.Groups["minor"].Value).$($match.Groups["patch"].Value)"
    }
}

function Compare-VersionParts {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    foreach ($part in @("Major", "Minor", "Patch")) {
        if ($Left.$part -lt $Right.$part) {
            return -1
        }

        if ($Left.$part -gt $Right.$part) {
            return 1
        }
    }

    return 0
}

function Get-HighestVersion {
    param([string[]]$Versions)

    $highest = $null
    foreach ($value in $Versions) {
        $candidate = Convert-ToVersionParts $value
        if ($null -eq $candidate) {
            continue
        }

        if ($null -eq $highest -or (Compare-VersionParts $candidate $highest) -gt 0) {
            $highest = $candidate
        }
    }

    return $highest
}

function Get-CurrentBranchName {
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REF) -and $env:GITHUB_REF.StartsWith("refs/heads/", [StringComparison]::Ordinal)) {
        return $env:GITHUB_REF.Substring("refs/heads/".Length)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
        return $env:GITHUB_REF_NAME
    }

    $branchName = (& git branch --show-current) -join ""
    if ($LASTEXITCODE -ne 0) {
        throw "git branch --show-current exited with code $LASTEXITCODE."
    }

    return $branchName.Trim()
}

function Get-ReleasePolicyVersion {
    param([string]$GitVersionMajorMinorPatch)

    $headTags = @(& git tag --points-at HEAD --list "v[0-9]*.[0-9]*.[0-9]*")
    if ($LASTEXITCODE -ne 0) {
        throw "git tag --points-at HEAD exited with code $LASTEXITCODE."
    }

    $headVersion = Get-HighestVersion $headTags
    if ($null -ne $headVersion) {
        return [pscustomobject]@{
            Version = $headVersion.Text
            Policy = "tag"
        }
    }

    $branchName = Get-CurrentBranchName
    if ($branchName -ne "main") {
        return [pscustomobject]@{
            Version = $GitVersionMajorMinorPatch
            Policy = "gitversion-$branchName"
        }
    }

    $configuredBase = Convert-ToVersionParts (Read-ConfiguredBaseVersion)
    $releaseTags = @(& git tag --list "v[0-9]*.[0-9]*.[0-9]*")
    if ($LASTEXITCODE -ne 0) {
        throw "git tag --list exited with code $LASTEXITCODE."
    }

    $latestRelease = Get-HighestVersion $releaseTags
    if ($null -eq $latestRelease) {
        return [pscustomobject]@{
            Version = $configuredBase.Text
            Policy = "configured-base"
        }
    }

    if ((Compare-VersionParts $latestRelease $configuredBase) -lt 0) {
        return [pscustomobject]@{
            Version = $configuredBase.Text
            Policy = "configured-base"
        }
    }

    return [pscustomobject]@{
        Version = "$($latestRelease.Major).$($latestRelease.Minor).$($latestRelease.Patch + 1)"
        Policy = "latest-tag-plus-one"
    }
}

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

$releasePolicy = Get-ReleasePolicyVersion $version
$version = $releasePolicy.Version
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
    "VersionPolicy=$($releasePolicy.Policy)" >> $env:GITHUB_OUTPUT
}

Write-Host "Release version policy: $($releasePolicy.Policy)"
Write-Host "GitVersion SemVer: $($gitVersion.SemVer)"
Write-Host "GitVersion MajorMinorPatch: $($gitVersion.MajorMinorPatch)"
Write-Host "Release SemVer: $version"
Write-Host "Release AssemblyVersion: $assemblyVersion"
Write-Host "Release FileVersion: $fileVersion"
Write-Host "GitVersion InformationalVersion: $($gitVersion.InformationalVersion)"
