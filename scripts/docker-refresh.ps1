#!/usr/bin/env pwsh
# Rebuilds and (re)starts the local Matmon review stack so the Docker containers at
# http://localhost:8099 (primary) and :8100 (sample probe) always reflect the current source.
#
# It uses the DEV stack (docker-compose.dev.yml: ./data bind-mounted, sample probe), plus the local,
# gitignored docker-compose.dev.override.yml when it exists - that one joins the primary to the local
# Matmon.Cloud's network (matmon-net) and carries the cloud bootstrap. Starting the dev file WITHOUT the
# override is what used to drop the cloud link after every rebuild; starting the plain docker-compose.yml
# instead brought up a different, unmounted primary under the same container name.
#
# Thanks to .dockerignore + Docker layer caching, turns that do not touch src/ are near-instant cache hits.
#
# Usage:
#   ./scripts/docker-refresh.ps1              # primary + probe-01
#   ./scripts/docker-refresh.ps1 primary      # just the primary

param(
    [string[]] $Services = @('primary', 'probe-01')
)

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $files = @('-f', 'docker-compose.dev.yml')
    if (Test-Path 'docker-compose.dev.override.yml') {
        $files += @('-f', 'docker-compose.dev.override.yml')
    }

    docker compose @files up -d --build @Services
}
finally {
    Pop-Location
}
