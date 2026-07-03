# Matmon

Matmon is a lightweight monitoring platform built with ASP.NET Core, C# and Docker.

It is designed as a compact, self-hosted alternative to classic monitoring tools: simple enough to run at home or in a small business network, but structured around probes, hosts, sensors, templates, alerts and notification rules.

## Features

- Docker-first deployment
- ASP.NET Core backend with a web-based UI
- Primary / secondary probe architecture
- Probe, folder, host and sensor tree
- Inherited settings, templates and credentials
- Sensor templates for reusable monitoring setups
- Channel-based sensor values with per-channel thresholds
- Dynamic sensor parameter fields based on sensor type
- Alerts with acknowledgement state
- Event log and sensor history
- Dark and bright UI themes
- Local and remote probe execution
- GitHub Actions Docker image build

## Sensor Types

Matmon currently includes sensor support for:

- Ping
- HTTP
- SNMP
- Synology NAS
- Proxmox PVE
- PowerShell / Windows Health
- SSL Certificate
- MSSQL Query
- TCP Port Check
- Probe Heartbeat
- Probe Health

## Architecture

Matmon uses a primary / secondary model.

The primary instance owns the UI, configuration, alerts, history and global state. Secondary probes connect outbound to the primary, receive assigned sensor work and report results back. This allows remote probes to run behind firewalls or NAT without exposing inbound ports.

Core structure:

- `Probe`: execution node, local or remote
- `Folder`: organizational grouping with inheritable settings
- `Host`: monitored device or endpoint
- `Sensor`: actual check, such as ping, SNMP, HTTP or PowerShell

Settings can be inherited from parents and templates. Sensors can override only the fields that differ from inherited defaults.

## Quick Start

Start the local primary and sample secondary probe:

```bash
docker compose up --build
```

Open the web UI:

```text
http://localhost:8099
```

Default login:

```text
Username: admin
Password: admin
```

The local secondary probe is exposed on:

```text
http://localhost:8100
```

## Configuration

Matmon is configured through environment variables.

Common settings:

```text
Matmon__Mode=Primary|Secondary
Matmon__WorkspacePath=data/workspace.json
Matmon__Auth__Username=admin
Matmon__Auth__Password=admin
Matmon__HeartbeatIntervalSeconds=30
Matmon__SeedSampleData=false
Matmon__ProvisionLocalDockerProbe=false
Matmon__ProvisionDemoSensors=false
Matmon__AutoCreateProbeSystemSensors=false
Matmon__CreateStarterMap=false
```

Secondary probe settings:

```text
Matmon__Mode=Secondary
Matmon__ProbeId=probe-01
Matmon__ProbeName=Remote Probe 01
Matmon__ProbeToken=probe-01-token
Matmon__PrimaryUrl=http://primary:8099
```

Runtime data is stored in `data/`.

The workspace file is generated automatically on first start if it does not exist. Runtime files such as `workspace.json`, backups, data protection keys and temporary files are not intended to be committed to Git.

New primary installations are plain by default: no hosts, sensors, demo probes or starter maps are created unless one of the explicit seed/provisioning flags is enabled. Existing Docker volumes keep their stored workspace, so remove or rename the volume if you want a truly fresh installation.

## Docker

The included `docker-compose.yml` starts:

- `primary` on port `8099`
- `probe-01` on port `8100`

Build manually:

```bash
docker compose build
```

Run detached:

```bash
docker compose up -d
```

Stop:

```bash
docker compose down
```

## Portable Primary Deployment

For a real primary installation on any Docker host, use the primary-only compose file. It pulls the published image from GitHub Container Registry:

```bash
cp .env.master.example .env.master
docker compose --env-file .env.master -f docker-compose.master.yml pull
docker compose --env-file .env.master -f docker-compose.master.yml up -d
```

Open Matmon from another device in the same network:

```text
http://<docker-host-ip>:8099
```

The portable compose uses `ghcr.io/real-ttx/matmon:latest`, binds the web UI to `0.0.0.0:8099` by default, stores runtime data in the Docker volume `matmon-data`, and maps `host.docker.internal` to the Docker host gateway. In normal bridge mode, the container can reach the LAN through the Docker host, which is the most portable setup across Linux, Windows and macOS Docker hosts.

The portable primary compose sets all sample/demo flags to `false`, so pulling a newer image does not create demo sensors. To reset an existing installation, stop the container and remove the named Docker volume:

```bash
docker compose --env-file .env.master -f docker-compose.master.yml down
docker volume rm matmon-data
```

On Linux only, you can alternatively run Matmon in the host network namespace:

```bash
cp .env.master.example .env.master
docker compose --env-file .env.master -f docker-compose.master.host-network.yml pull
docker compose --env-file .env.master -f docker-compose.master.host-network.yml up -d
```

Host networking makes Matmon behave more like a native process on the Docker host, but it is not portable to Docker Desktop. Use the normal `docker-compose.master.yml` unless you specifically need host networking.

To build from a local checkout instead of pulling GHCR:

```bash
docker compose --env-file .env.master -f docker-compose.master.yml -f docker-compose.master.build.yml up -d --build
```

### Caddy Reverse Proxy

If Caddy runs directly on the Docker host and Matmon exposes port `8099`:

```caddyfile
matmon.example.com {
    reverse_proxy 127.0.0.1:8099
}
```

If Caddy runs in Docker, `127.0.0.1` is the Caddy container, not the Docker host. Prefer putting Caddy on the `matmon` Docker network and proxying to the service name:

```caddyfile
matmon.example.com {
    reverse_proxy matmon-primary:8099
}
```

For the Linux host-network compose, a Dockerized Caddy container must proxy to the Docker host gateway instead:

```caddyfile
matmon.example.com {
    reverse_proxy host.docker.internal:8099
}
```

In that case the Caddy container also needs `host.docker.internal:host-gateway` configured as an extra host.

## GitHub Actions

The repository includes a Docker build and publish workflow:

```text
.github/workflows/docker-image.yml
```

The workflow runs on:

- `push`
- `pull_request`
- `workflow_dispatch`

Pull requests only build the image. Pushes publish to GitHub Container Registry using the repository name, for example `ghcr.io/real-ttx/matmon:latest` on the default branch.

## Development

Requirements:

- .NET 10 SDK
- Docker Desktop or another Docker-compatible runtime

Build the solution:

```bash
dotnet build Matmon.slnx
```

Run locally with hot reload (rebuilds and reloads on every change, served at http://localhost:5084):

```powershell
./scripts/dev.ps1
```

This wraps `dotnet watch run` for `src/Matmon.Host`. Keep it running in a terminal — Razor and CSS/JS changes refresh live, C# changes trigger a quick rebuild and restart, so the browser always shows the current code.

Run locally through Docker:

```bash
docker compose up -d --build
```

Important code locations:

- `src/Matmon.Host/Program.cs`: application startup, auth, APIs and runtime mode
- `src/Matmon.Host/Services`: polling, probes, persistence and dashboard services
- `src/Matmon.Host/Pages`: Razor Pages web UI
- `src/Matmon.Core/Domain`: sensor definitions, executors and monitoring domain model
- `src/Matmon.Core/Sample`: default sample topology

## Status

Matmon is under active development. The current focus is building a practical, visual monitoring experience with configurable sensors, templates, inherited credentials, remote probes, alert acknowledgement and scalable history handling.

## License

Matmon is **proprietary** software — see the [LICENSE](LICENSE). Commercial use is
governed by the applicable Matmon product plan (Free / Business / Enterprise).
Bundled third-party components keep their own licenses.
