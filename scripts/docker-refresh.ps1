#!/usr/bin/env pwsh
# Rebuilds and (re)starts the local Matmon primary so the Docker container at
# http://localhost:8099 always reflects the current source.
#
# Designed to be run automatically by a Claude Code Stop hook after each change.
# Thanks to .dockerignore + Docker layer caching, turns that do not touch src/
# are near-instant cache hits; only real source changes trigger a real rebuild.
#
# Usage:
#   ./scripts/docker-refresh.ps1

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    docker compose up -d --build primary
}
finally {
    Pop-Location
}
