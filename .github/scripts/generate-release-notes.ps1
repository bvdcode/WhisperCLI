param(
    [Parameter(Mandatory = $true)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [string]$Commit = 'HEAD',
    [Parameter(Mandatory = $true)][string]$Repository,
    [string]$ServerUrl = 'https://github.com',
    [string]$OutputFile = 'artifacts/release-notes.md'
)

$ErrorActionPreference = 'Stop'

function Read-Git {
    param([string[]]$GitArguments)

    $output = @(& git @GitArguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

$commitSha = (Read-Git @('rev-parse', '--verify', "$Commit^{commit}")) -join ''
$version = [version]$Tag.Substring(1)
$previousTag = $null
$previousVersion = $null
# Ignore unrelated branch tags, prereleases, and this release's own tag on reruns.
foreach ($candidate in (Read-Git @('tag', '--merged', $commitSha, '--list', 'v[0-9]*.[0-9]*.[0-9]*'))) {
    if ($candidate -notmatch '^v\d+\.\d+\.\d+$') { continue }
    $candidateVersion = [version]$candidate.Substring(1)
    if ($candidateVersion -lt $version -and ($null -eq $previousVersion -or $candidateVersion -gt $previousVersion)) {
        $previousTag = $candidate
        $previousVersion = $candidateVersion
    }
}

$range = if ($previousTag) { "$previousTag..$commitSha" } else { $commitSha }
$commits = @(Read-Git @('log', '--reverse', '--topo-order', '--format=%H%x09%s', $range, '--'))
$repositoryUrl = "$($ServerUrl.TrimEnd('/'))/$Repository"
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('## Changes')
$lines.Add('')
if ($previousTag) {
    $lines.Add("Changes since [$previousTag]($repositoryUrl/releases/tag/$previousTag).")
}
else {
    $lines.Add('Initial release.')
}
$lines.Add('')
foreach ($entry in $commits) {
    $sha, $subject = $entry -split "`t", 2
    # Commit subjects are text, not Markdown supplied by the release workflow.
    $subject = [regex]::Replace($subject, '([\\`*_{}\[\]<>])', '\$1')
    $lines.Add("- $subject ([$($sha.Substring(0, 7))]($repositoryUrl/commit/$sha))")
}
if ($commits.Count -eq 0) {
    $lines.Add('No new commits.')
}
if ($previousTag) {
    $lines.Add('')
    $lines.Add("**Full changelog:** [$previousTag...$Tag]($repositoryUrl/compare/$previousTag...$Tag)")
}

$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[System.IO.File]::WriteAllText($outputPath, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated notes for $Tag from $range ($($commits.Count) commits): $outputPath"
