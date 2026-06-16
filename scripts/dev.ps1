#!/usr/bin/env pwsh
# Local development with hot reload.
#
# Runs the Matmon host through `dotnet watch`, so every change you (or an
# assistant) make on disk is rebuilt and reloaded automatically — Razor pages
# and CSS/JS refresh live, C# changes trigger a quick rebuild + restart. Just
# keep this running in a terminal and the browser at http://localhost:5084
# always shows the current code. The first run opens the browser for you.
#
# Usage:
#   ./scripts/dev.ps1

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $projectRoot 'src/Matmon.Host'

$env:ASPNETCORE_ENVIRONMENT = 'Development'

Write-Host 'Starting Matmon with hot reload on http://localhost:5084 (Ctrl+C to stop)...' -ForegroundColor Cyan

Push-Location $hostProject
try {
    dotnet watch run
}
finally {
    Pop-Location
}
