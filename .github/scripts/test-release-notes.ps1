$ErrorActionPreference = 'Stop'
$generator = Join-Path $PSScriptRoot 'generate-release-notes.ps1'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$fixture = Join-Path $tempRoot ("whispercli-release-notes-" + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($fixture) | Out-Null

function Test-Git {
    param([string[]]$GitArguments)

    $output = @(& git @GitArguments)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($GitArguments -join ' ')" }
    return $output
}

function Assert-Contains {
    param([string]$Text, [string]$Expected)
    if (-not $Text.Contains($Expected)) { throw "Release notes are missing: $Expected" }
}

function Assert-NotContains {
    param([string]$Text, [string]$Unexpected)
    if ($Text.Contains($Unexpected)) { throw "Release notes unexpectedly contain: $Unexpected" }
}

Push-Location $fixture
try {
    Test-Git @('init', '--quiet', '--initial-branch=main')
    Test-Git @('config', 'user.name', 'Release Notes Test')
    Test-Git @('config', 'user.email', 'release-notes@example.invalid')
    Test-Git @('config', 'commit.gpgsign', 'false')
    Test-Git @('config', 'tag.gpgsign', 'false')
    Test-Git @('config', 'core.hooksPath', (Join-Path $fixture 'no-hooks'))
    Test-Git @('commit', '--quiet', '--allow-empty', '-m', 'chore: initial commit')
    $initialSha = (Test-Git @('rev-parse', 'HEAD')) -join ''

    & $generator -Tag 'v1.0.0' -Repository 'test/project' -OutputFile 'first.md'
    $first = [System.IO.File]::ReadAllText((Join-Path $fixture 'first.md'))
    Assert-Contains $first 'Initial release.'
    Assert-Contains $first 'chore: initial commit'
    Assert-Contains $first "https://github.com/test/project/commit/$initialSha"
    Assert-NotContains $first '/compare/'

    Test-Git @('tag', '-a', 'v1.0.0', '-m', 'First release')
    # A newer tag on an unrelated branch must not hide main's commits.
    Test-Git @('checkout', '--quiet', '-b', 'unrelated')
    Test-Git @('commit', '--quiet', '--allow-empty', '-m', 'fix: unrelated change')
    Test-Git @('tag', 'v1.0.1')
    Test-Git @('checkout', '--quiet', 'main')
    $unicode = [string][char]0x0416
    Test-Git @('commit', '--quiet', '--allow-empty', '-m', "fix: punctuation [ru] <text> $unicode")
    Test-Git @('tag', 'v1.0.2-rc.1')
    Test-Git @('commit', '--quiet', '--allow-empty', '-m', 'feat: detect speech')
    $releaseSha = (Test-Git @('rev-parse', 'HEAD')) -join ''

    & $generator -Tag 'v1.0.2' -Commit $releaseSha -Repository 'test/project' -OutputFile 'release.md'
    $notes = [System.IO.File]::ReadAllText((Join-Path $fixture 'release.md'))
    Assert-Contains $notes "fix: punctuation \[ru\] \<text\> $unicode"
    Assert-Contains $notes 'feat: detect speech'
    Assert-Contains $notes 'https://github.com/test/project/compare/v1.0.0...v1.0.2'
    Assert-NotContains $notes 'chore: initial commit'
    Assert-NotContains $notes 'unrelated change'
    if ([regex]::Matches($notes, '(?m)^- ').Count -ne 2) { throw 'Expected exactly two commits since the previous release.' }

    # Creating the release tag and later advancing main must not change a rerun.
    Test-Git @('tag', 'v1.0.2')
    Test-Git @('commit', '--quiet', '--allow-empty', '-m', 'fix: future change')
    Test-Git @('tag', 'v1.0.3')
    & $generator -Tag 'v1.0.2' -Commit $releaseSha -Repository 'test/project' -OutputFile 'rerun.md'
    $rerun = [System.IO.File]::ReadAllText((Join-Path $fixture 'rerun.md'))
    if ($rerun -cne $notes) { throw 'Rerunning a tagged release changed its notes.' }

    & $generator -Tag 'v1.0.4' -Repository 'test/project' -OutputFile 'empty.md'
    $empty = [System.IO.File]::ReadAllText((Join-Path $fixture 'empty.md'))
    Assert-Contains $empty 'No new commits.'
    Assert-Contains $empty '/compare/v1.0.3...v1.0.4'
    Assert-NotContains $empty 'fix: future change'

    Write-Host 'Release notes checks passed: first release, direct commits, annotated tags, unrelated/prerelease tags, Markdown/Unicode, reruns, and empty ranges.'
}
finally {
    Pop-Location
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixture)
    $expectedParent = $tempRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ([System.IO.Path]::GetDirectoryName($resolvedFixture) -ne $expectedParent -or
        -not [System.IO.Path]::GetFileName($resolvedFixture).StartsWith('whispercli-release-notes-')) {
        throw "Refusing to remove a test directory outside the expected temporary path: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
