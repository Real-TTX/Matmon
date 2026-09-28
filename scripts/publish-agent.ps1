#!/usr/bin/env pwsh
# Publishes the Matmon agent for every platform the instance offers, into the folder a LOCAL dev run
# serves them from (src/Matmon.Host/agent/<rid>/). The Docker image does the same in its own build stage,
# so this is only needed to see downloads on the Agents page while running the Host from source.
#
# How the agent is packaged (self-contained, single file, compressed) lives in Matmon.Agent.csproj; this
# script only chooses platforms, output folder and the version to bake in.
#
# Usage:
#   ./scripts/publish-agent.ps1                     # all platforms
#   ./scripts/publish-agent.ps1 -RuntimeIds win-x64  # just one
#   ./scripts/publish-agent.ps1 -Version 0.1.42      # bake an explicit version (default: none -> local-<date>)

param(
    [string[]] $RuntimeIds = @('win-x64', 'linux-x64', 'linux-arm64'),
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$agentProject = Join-Path $projectRoot 'src/Matmon.Agent/Matmon.Agent.csproj'
$outputRoot = Join-Path $projectRoot 'src/Matmon.Host/agent'

foreach ($rid in $RuntimeIds) {
    $output = Join-Path $outputRoot $rid
    Write-Host "Publishing matmon-agent for $rid -> $output" -ForegroundColor Cyan
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }

    $arguments = @('publish', $agentProject, '-c', 'Release', '-r', $rid, '-o', $output, '-nologo', '-v', 'q')
    if ($Version) { $arguments += "-p:MatmonVersion=$Version" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
}

foreach ($file in Get-ChildItem $outputRoot -Recurse -File) {
    Write-Host ("{0,-40} {1,8:N1} MB" -f $file.FullName.Substring($outputRoot.Length + 1), ($file.Length / 1MB))
}
