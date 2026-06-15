# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code — update it whenever architecture, persistence, interfaces, or major data flow change.

## What Matmon is

A lightweight, self-hosted network monitoring platform (a compact PRTG-style alternative) for home / small-business networks. ASP.NET Core on **.NET 10**, C#, Razor Pages UI, Docker-first. MIT licensed. Published image: `ghcr.io/real-ttx/matmon:latest`.

## Build / run / verify

- **Requirements:** .NET 10 SDK (developed against `10.0.203`), Docker Desktop or compatible runtime.
- **Build:** `dotnet build Matmon.slnx` (note: `.slnx`, not a classic `.sln`).
- **Run locally (dev):** `dotnet run --project src/Matmon.Host` → http://localhost:5084 (`ASPNETCORE_ENVIRONMENT=Development`). Default login `admin` / `admin`.
- **Run via Docker:** `docker compose up --build` → primary on http://localhost:8099, sample secondary probe on http://localhost:8100.
- **Health check:** `GET /healthz` and `GET /api/mode` (both anonymous).
- **Tests:** `dotnet test tests/Matmon.Tests/Matmon.Tests.csproj` (xunit). Baseline covers the pure core logic (threshold parsing/evaluation, channel-threshold escalation, schedule calculator, settings inheritance). Coverage grows with each refactor.

## Solution layout

Two projects under `src/`:

- **`Matmon.Core`** — framework-free domain. `Domain/` holds the monitoring model, the `ISensorExecutor` implementations, inheritance/threshold/schedule logic, notifications. `Sample/` holds the default sample topology (`SampleTopologyFactory`).
- **`Matmon.Host`** — the web app.
  - `Program.cs` — startup, cookie auth, authorization policies, minimal APIs (probe heartbeat/assignments/observations/discovery), sensor-executor DI registration, mode-dependent hosted services.
  - `Services/` — persistence, polling, probe registry, dashboard/config providers, backup scheduler, slave (secondary) workers.
  - `Pages/` — Razor Pages UI (`.cshtml` + `.cshtml.cs`). Shared layout in `Pages/Shared/_Layout.cshtml`.
  - `wwwroot/` — `css/site.css` (large, ~9k lines), `js/site.js`, Bootstrap 5 + jQuery under `lib/`.

## Architecture: primary / secondary

Mode is set via `Matmon__Mode=Primary|Secondary` (default Primary).

- **Primary** owns the UI, configuration, alerts, history and global state. Registers `SensorPollingService` + `BackupSchedulerService`. Exposes the `/api/probes/*` endpoints that secondaries call.
- **Secondary** connects **outbound** to the primary (works behind firewalls/NAT), pulls assigned sensor work, executes it, and POSTs results back. Registers `SlaveHeartbeatService` + `SlaveSensorWorker`.
- Probe auth: a per-probe token sent as `X-Matmon-Probe-Token` header or `?token=` query (`ReadProbeToken` in `Program.cs`; validated via `IMonitoringWorkspaceStore.TryValidateProbe`).

## Domain model

Tree of `MonitoringElement`s: `ProbeElement` → `FolderElement` → `HostElement` → `SensorElement` (see `Matmon.Core/Domain`). Settings, credentials and templates are **inheritable** from parents; a sensor overrides only the fields that differ (`MonitoringInheritanceResolver`). Templates: `MonitoringTemplate` (+ `TemplateEditor` page). Thresholds per channel: `SensorThresholdEvaluator`. Scheduling: `MonitoringSchedule` / `MonitoringScheduleCalculator`.

**Sensors** are `ISensorExecutor` implementations registered in `RegisterSensorExecutors` (`Program.cs`): Ping, HTTP (+ HttpAdvanced), SNMP (+ interface/UPS), Synology NAS/Health, Proxmox PVE, PowerShell/Windows Health, Windows Service/Process, Linux SSH Health, SSL Certificate (+ chain), MSSQL Query, TCP Port, DNS, NTP, Docker Container, Backup Job, Disk SMART, Probe Heartbeat, Probe Health. Adding a sensor type = new `ISensorExecutor` + registration + parameter definitions (`SensorParameterDefinition`).

## Persistence (important — and the current bottleneck)

App state lives in a single JSON workspace file (`data/workspace.json`, configurable via `Matmon__WorkspacePath`), loaded and held entirely in memory by `Services/InMemoryMonitoringWorkspaceStore.cs` (~4530 lines — the central god-class). The state is one private `WorkspaceDocument` with 13 collections, guarded by `_gate`. There is **no relational database for app state**; the `Microsoft.Data.SqlClient` dependency is only for the MSSQL *sensor*.

- Saves are debounced via a timer with two priorities: `Configuration` (~750ms) and `Telemetry` (~10s, max 30s dirty delay). `SaveDocumentLocked` serializes the **entire** document to a temp file then atomically moves it; a full-file `.bak` copy is refreshed every 5 min. Section-based backups live under `data/backups/`.
- Data-protection keys: `data/dataprotection-keys`. Runtime `data/` is gitignored.
- **Known scaling bottleneck:** the three unbounded collections — `SensorHistory` (`List<SensorObservation>`), `Events`, `SensorStatistics` (hourly buckets) — are serialized in full on every telemetry save. This is unworkable at hundreds of MB. Retention defaults: observations 7d, events 30d, statistics 90d. In-memory indexes: `_sensorHistoryBySensor`, `_latestSensorObservations`. `IMonitoringWorkspaceStore` currently mixes config + telemetry + backup and is the seam to split.
- **Telemetry rework in progress (Phase A):** `Matmon.Core/Telemetry` now holds `ITelemetryRepository` (storage contract for observations/events/statistics) and an embedded `SqliteTelemetryRepository` (WAL, `Microsoft.Data.Sqlite`). It is **not wired into the store yet** — the next step (A2) is to make `InMemoryMonitoringWorkspaceStore` delegate telemetry storage to it, migrate existing `workspace.json` history into `telemetry.db` once on startup, drop those collections from the JSON, and route backup/restore + cleanup through it. Until then telemetry still lives in the JSON document.

## Auth & authorization

Cookie auth (`Program.cs`). Roles `Admin` / `User` (`MatmonUserRole`). Admin-only pages are declared with `AuthorizePage(..., MatmonSecurity.AdminPolicy)` conventions; a global `MatmonPageWriteGuard` MVC filter enforces write protection. API paths return 401/403 (JSON) instead of redirecting. Passwords hashed via `MatmonPasswordHasher`. Credentials encrypted at rest via ASP.NET DataProtection (`HydrateCredentialBundles` / `ProtectCredentialBundles`).

## Configuration (env vars)

All under the `Matmon__` prefix (see `appsettings.json` + README). Common: `Matmon__Mode`, `Matmon__WorkspacePath`, `Matmon__Auth__Username|Password`, `Matmon__HeartbeatIntervalSeconds`. Seeding/provisioning flags default to false: `SeedSampleData`, `ProvisionLocalDockerProbe`, `ProvisionDemoSensors`, `AutoCreateProbeSystemSensors`, `CreateStarterMap`. Secondary: `ProbeId`, `ProbeName`, `ProbeToken`, `PrimaryUrl`.

## Docker

`Dockerfile` at `src/Matmon.Host/Dockerfile` (SDK build → aspnet runtime; installs PowerShell + ssh + NTLM for sensors; binds `:8099`). Compose files: `docker-compose.yml` (local primary + sample probe), `docker-compose.master*.yml` (portable primary, GHCR pull / host-network / local build). CI: `.github/workflows/docker-image.yml` builds on PR, publishes to GHCR on push.

## Conventions

- C# nullable + implicit usings on; modern C# (collection expressions `[]`, records, pattern matching). Keep new code matching the surrounding style.
- Razor Pages, one page = `Xxx.cshtml` + `Xxx.cshtml.cs`. Create/Edit dialogs are the `*Create` / `*Edit` / `*Editor` pages.
- All workspace mutations go through `IMonitoringWorkspaceStore` and must run under its `_gate`; they call `QueueSave(SavePriority.*)`.
- Keep the build green (`dotnet build Matmon.slnx`) after each change.

## Known gaps / cleanup targets

- **Thin test coverage** — `Matmon.Tests` now exists with a baseline for core pure logic; the tree-level `MonitoringInheritanceResolver`, telemetry retention/migration and the executors are still uncovered.
- `InMemoryMonitoringWorkspaceStore` is a god-class mixing many concerns; decompose into focused services.
- JSON-everything persistence does not scale (see above) — SQLite telemetry migration is the planned fix.
