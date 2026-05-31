#!/bin/sh
set -eu

IMAGE="${MATMON_IMAGE:-ghcr.io/real-ttx/matmon:latest}"
CONTAINER_NAME="${MATMON_CONTAINER_NAME:-matmon-probe}"
DATA_VOLUME="${MATMON_DATA_VOLUME:-matmon-probe-data}"
HTTP_PORT="${MATMON_HTTP_PORT:-8099}"
HEARTBEAT_SECONDS="${MATMON_HEARTBEAT_SECONDS:-30}"
ADMIN_USER="${MATMON_ADMIN_USER:-admin}"
ADMIN_PASSWORD="${MATMON_ADMIN_PASSWORD:-admin}"

if [ -z "${MATMON_PROBE_ID:-}" ]; then
  echo "MATMON_PROBE_ID is required." >&2
  exit 1
fi

if [ -z "${MATMON_PROBE_TOKEN:-}" ]; then
  echo "MATMON_PROBE_TOKEN is required." >&2
  exit 1
fi

if [ -z "${MATMON_MASTER_URL:-}" ]; then
  echo "MATMON_MASTER_URL is required." >&2
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

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  -p "$HTTP_PORT:8099" \
  -v "$DATA_VOLUME:/app/data" \
  -e ASPNETCORE_URLS=http://+:8099 \
  -e Matmon__Mode=Slave \
  -e "Matmon__ProbeId=$MATMON_PROBE_ID" \
  -e "Matmon__ProbeName=$PROBE_NAME" \
  -e "Matmon__ProbeToken=$MATMON_PROBE_TOKEN" \
  -e "Matmon__MasterUrl=$MATMON_MASTER_URL" \
  -e "Matmon__HeartbeatIntervalSeconds=$HEARTBEAT_SECONDS" \
  -e Matmon__WorkspacePath=/app/data/workspace.json \
  -e "Matmon__Auth__Username=$ADMIN_USER" \
  -e "Matmon__Auth__Password=$ADMIN_PASSWORD" \
  "$IMAGE"

echo "Matmon probe '$PROBE_NAME' started as container '$CONTAINER_NAME'."
echo "Local probe UI: http://localhost:$HTTP_PORT"
