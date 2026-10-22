#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Copies the MatUi toolkit (CSS, scripts, icon sprite, changelog, manifest) from a MatDesign archive into src/Matmon.Host/wwwroot/matui.
.DESCRIPTION
    Apps copy MatUi, they never load it from another site (rule CODE-03), and the toolkit has a version of its own (MatDesign docs/VERSIONING.md): the files of one
    version belong together, so all of them are copied, never a few. The archive is what MatDesign serves at /ui/matui.zip (the toolkit as it is now) or a release of the
    Studio (/releases/<version>/matui-<version>.zip). The files are checked against the SHA-256 in matui.manifest.json that came with the archive.
    What was copied is written to scripts/matui-synced.txt (version, hash, the commit of MatDesign when it is known), which check-matui.ps1 compares with the news of MatDesign.
    Read the changelog (wwwroot/matui/CHANGELOG.md) from the old version to the new one before committing: a Breaking section says what the app has to change.
.PARAMETER Archive
    The address or the path of matui.zip (default: the MatDesign of this PC, http://localhost:8060/ui/matui.zip).
.PARAMETER Commit
    The commit of the MatDesign repository the archive was made from, when known; it is only written down.
#>
param(
    [string]$Archive = 'http://localhost:8060/ui/matui.zip',
    [string]$Commit = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'src/Matmon.Host/wwwroot/matui'
$work = Join-Path ([IO.Path]::GetTempPath()) ('matui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

try {
    $zip = Join-Path $work 'matui.zip'
    if ($Archive -match '^https?://') { Invoke-WebRequest $Archive -OutFile $zip -UseBasicParsing } else { Copy-Item $Archive $zip }
    Expand-Archive $zip (Join-Path $work 'x')
    $folder = Get-ChildItem (Join-Path $work 'x') -Directory | Select-Object -First 1
    if (-not $folder) { throw "No folder in $Archive" }

    $manifestPath = Join-Path $folder.FullName 'matui.manifest.json'
    if (-not (Test-Path $manifestPath)) { throw 'The archive has no matui.manifest.json: it is not a MatUi archive of a version that is numbered.' }
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

    # the files as they came must be the files the manifest names
    foreach ($f in $manifest.files) {
        $hash = (Get-FileHash (Join-Path $folder.FullName $f.name) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $f.sha256) { throw "$($f.name) does not match the manifest (SHA-256): the archive is damaged or was changed." }
    }

    # everything the archive holds (the bundle, the script, the sprite, the parts of the CSS, the changelog, the manifest): the files of one version belong together
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $folder.FullName '*') $target -Recurse -Force

    $note = "MatUi $($manifest.version) (hash $($manifest.hash), build $($manifest.build))`ncommit of MatDesign: $(if ($Commit) { $Commit } else { 'not given' })`nsynced: $(Get-Date -Format 'yyyy-MM-dd')`n"
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'matui-synced.txt'), $note, (New-Object Text.UTF8Encoding($false)))
    Write-Host "MatUi $($manifest.version) ($($manifest.hash)) copied to $target" -ForegroundColor Green
    Write-Host 'Next: read wwwroot/matui/CHANGELOG.md for what the update takes with it, build, redeploy, look at the pages.'
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
