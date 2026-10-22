#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Says whether MatDesign has something new for the toolkit copy in src/Matmon.Host/wwwroot/matui.
.DESCRIPTION
    Asks the sibling repository MatDesign what is new (git fetch, then the commits that touched the toolkit or its changelog since the commit that scripts/sync-matui.ps1
    wrote down in scripts/matui-synced.txt) and lets tools/matui-check.js of MatDesign compare the copy with the changelog of origin/main (whether a file was changed by hand,
    which versions come after the copy, whether one breaks something). Nothing is changed: when there is news, build the toolkit (MatDesign: ./scripts/redeploy.ps1, or
    dotnet run in Development) and copy it with sync-matui.ps1.
.PARAMETER Source
    The MatDesign repository (default: the sibling directory MatDesign).
#>
param(
    [string]$Source = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'MatDesign')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Source '.git'))) { throw "MatDesign not found at $Source" }
$ui = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Matmon.Host/wwwroot'
$synced = Join-Path $PSScriptRoot 'matui-synced.txt'

git -C $Source fetch --quiet origin
$upstream = (git -C $Source rev-parse origin/main).Trim()
$known = if (Test-Path $synced) { (Select-String -Path $synced -Pattern '^commit of MatDesign: ([0-9a-f]{7,40})').Matches | ForEach-Object { $_.Groups[1].Value } | Select-Object -First 1 } else { $null }
Write-Host "MatDesign origin/main: $($upstream.Substring(0, 10))   copy made from: $(if ($known) { $known.Substring(0, 10) } else { 'unknown' })"

$news = @()
if ($known) { $news = @(git -C $Source log --oneline "$known..origin/main" -- src/MatUi CHANGELOG.md docs/VERSIONING.md) }
if ($known -and $news.Count -eq 0) {
    Write-Host 'Nothing new for the toolkit since the copy.' -ForegroundColor Green
} elseif ($news.Count -gt 0) {
    Write-Host "$($news.Count) change(s) to the toolkit since the copy:" -ForegroundColor Yellow
    $news | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host 'The commit of the copy is not known: look at the changelog below.' -ForegroundColor Yellow
}

# the copy against the changelog of origin/main (the working copy of MatDesign may be behind)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('matui-changelog-' + [guid]::NewGuid().ToString('N') + '.md')
try {
    git -C $Source show origin/main:CHANGELOG.md | Set-Content $tmp -Encoding utf8
    node (Join-Path $Source 'tools/matui-check.js') $ui --changelog $tmp
    exit $LASTEXITCODE
}
finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}
