#!/bin/sh
set -eu

IMAGE="${MATMON_IMAGE:-ghcr.io/real-ttx/matmon:latest}"
CONTAINER_NAME="${MATMON_CONTAINER_NAME:-matmon-probe}"
DATA_VOLUME="${MATMON_DATA_VOLUME:-matmon-probe-data}"
HTTP_PORT="${MATMON_HTTP_PORT:-8099}"
HEARTBEAT_SECONDS="${MATMON_HEARTBEAT_SECONDS:-30}"
PRIMARY_URL="${MATMON_PRIMARY_URL:-${MATMON_MASTER_URL:-}}"
# The Docker socket lets the primary update this probe (Probes > Update). It gives the container control over Docker
# on this machine; MATMON_DOCKER_SOCKET= (empty) leaves it out - the probe is then updated by hand.
DOCKER_SOCKET="${MATMON_DOCKER_SOCKET-/var/run/docker.sock}"

if [ -z "${MATMON_PROBE_ID:-}" ]; then
  echo "MATMON_PROBE_ID is required." >&2
  exit 1
fi

if [ -z "${MATMON_PROBE_TOKEN:-}" ]; then
  echo "MATMON_PROBE_TOKEN is required." >&2
  exit 1
fi

if [ -z "$PRIMARY_URL" ]; then
  echo "MATMON_PRIMARY_URL is required." >&2
  exit 1
fi

PROBE_NAME="${MATMON_PROBE_NAME:-$MATMON_PROBE_ID}"

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required but was not found in PATH." >&2
  exit 1
fi

if docker ps -a --format '{{.Names}}' | grep -Fxq "$CONTAINER_NAME"; then
  docker rm -f "$CONTAINER_NAME" >/dev/null
fi

docker volume create "$DATA_VOLUME" >/dev/null
docker pull "$IMAGE"

set -- -v "$DATA_VOLUME:/app/data"
if [ -n "$DOCKER_SOCKET" ] && [ -S "$DOCKER_SOCKET" ]; then
  set -- "$@" -v "$DOCKER_SOCKET:/var/run/docker.sock"
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  -p "$HTTP_PORT:8099" \
  "$@" \
  -e ASPNETCORE_URLS=http://+:8099 \
  -e Matmon__Mode=Secondary \
  -e "Matmon__ProbeId=$MATMON_PROBE_ID" \
  -e "Matmon__ProbeName=$PROBE_NAME" \
  -e "Matmon__ProbeToken=$MATMON_PROBE_TOKEN" \
  -e "Matmon__PrimaryUrl=$PRIMARY_URL" \
  -e "Matmon__HeartbeatIntervalSeconds=$HEARTBEAT_SECONDS" \
  -e Matmon__WorkspacePath=/app/data/workspace.json \
  "$IMAGE"

echo "Matmon probe '$PROBE_NAME' started as container '$CONTAINER_NAME'."
echo "Status page: http://localhost:$HTTP_PORT"
if [ -n "$DOCKER_SOCKET" ] && [ -S "$DOCKER_SOCKET" ]; then
  echo "The primary can update this probe (Probes > Update)."
else
  echo "No Docker socket mounted: update this probe by pulling the new image and running this script again."
fi
