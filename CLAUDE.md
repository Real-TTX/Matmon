# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code — update it whenever architecture, persistence, interfaces, or major data flow change.

## What Matmon is

A lightweight, self-hosted network monitoring platform (a compact PRTG-style alternative) for home / small-business networks. ASP.NET Core on **.NET 10**, C#, Razor Pages UI, Docker-first. MIT licensed. Published image: `ghcr.io/real-ttx/matmon:latest`.

## Build / run / verify

- **Requirements:** .NET 10 SDK (developed against `10.0.203`), Docker Desktop or compatible runtime.
- **Build:** `dotnet build Matmon.slnx` (note: `.slnx`, not a classic `.sln`).
- **Run locally (dev):** `dotnet run --project src/Matmon.Host` → http://localhost:5084 (`ASPNETCORE_ENVIRONMENT=Development`). Default login `admin` / `admin`. For a live loop use `./scripts/dev.ps1` (`dotnet watch run`) — rebuilds/reloads on every change so the browser always shows current code.
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

Tree of `MonitoringElement`s: `ProbeElement` → `FolderElement` → `HostElement` → `SensorElement` (see `Matmon.Core/Domain`). Settings and credentials are **inheritable** from parents; a sensor overrides only the fields that differ (`MonitoringInheritanceResolver`). Thresholds per channel: `SensorThresholdEvaluator`. Scheduling: `MonitoringSchedule` / `MonitoringScheduleCalculator`.

**Templates** (`MonitoringTemplate` + `TemplateEditor`) are **copy + origin**, not live inheritance. Applying a template (at sensor creation, or via the edit page / Templates "apply") bakes the template's resolved settings into the element's own `Settings` and records `MonitoringElement.TemplateOriginId`. Editing the template afterwards does **not** change existing elements; a "Restore from template" action re-copies the current template values (template wins). `ApplyTemplateCopy` (Workspace.cshtml.cs) is the single entry point; create lets the user's form values win, re-apply/restore lets the template win. The legacy live-inheritance list `AppliedTemplateIds` is no longer populated — `MigrateAppliedTemplatesToCopies` (store ctor) bakes any remaining links into `Settings` + sets the origin + clears the list on load. The TemplateEditor "Impact" tab lists sensors whose origin (directly or via the template's parent chain) is this template. Templates still chain via `ParentTemplateId` (resolved when copied).

**Sensors** are `ISensorExecutor` implementations registered in `RegisterSensorExecutors` (`Program.cs`): Ping, HTTP (+ HttpAdvanced), SNMP (+ interface/UPS), Synology NAS/Health, Proxmox PVE, UniFi Health (cloud api.ui.com / local controller, X-API-KEY — availability, not SMART), PowerShell/Windows Health, Windows Service/Process, Linux SSH Health, SSL Certificate (+ chain), MSSQL Query, TCP Port, DNS, NTP, Docker Container, Backup Job, Disk SMART, Probe Heartbeat, Probe Health. Adding a sensor type = new `ISensorExecutor` + registration + parameter definitions (`SensorParameterDefinition`).

## Persistence (important — and the current bottleneck)

App state lives in a single JSON workspace file (`data/workspace.json`, configurable via `Matmon__WorkspacePath`), loaded and held entirely in memory by `Services/InMemoryMonitoringWorkspaceStore.cs` (~3700 lines; still large — decomposition is Phase B, in progress). The state is one private `WorkspaceDocument` with 13 collections, guarded by `_gate`. The class is now `partial`, split by concern into sibling files: `InMemoryMonitoringWorkspaceStore.Backup.cs` (backup/restore), `.Persistence.cs` (load/save: `LoadDocument`, the debounced `QueueSave`/`SaveDocumentLocked`, `.bak` refresh) and `.Telemetry.cs` (the telemetry facade: `RecordSensorObservation`, history/event/statistics getters, `RunTelemetryMaintenance` rollup+retention, `CleanupStorage`, `MigrateDocumentTelemetryIntoRepository`). The core `.cs` (~3100 lines) keeps topology/template/notification/user/map CRUD, alerts, credentials, defaults and the sensor-definition catalog. There is **no relational database for app state**; the `Microsoft.Data.SqlClient` dependency is only for the MSSQL *sensor*.

- Saves are debounced via a timer with two priorities: `Configuration` (~750ms) and `Telemetry` (~10s, max 30s dirty delay). `SaveDocumentLocked` serializes the **entire** document to a temp file then atomically moves it; a full-file `.bak` copy is refreshed every 5 min. Section-based backups live under `data/backups/`.
- Data-protection keys: `data/dataprotection-keys`. Runtime `data/` is gitignored.
- **Retention & statistics are per-sensor-type by default.** `Matmon.Core/Telemetry/SensorTelemetryProfiles` maps each sensor type to a profile (raw-observation days, statistics bucket minutes, statistics-retention days, event-retention days): *Responsive metrics* for latency/throughput sensors (raw 3d, hourly buckets, stats 365d), *Availability* for up/down sensors (raw 14d, daily buckets, stats 365d), *Infrastructure* for probe sensors, and *General* as the fallback (raw 7d, hourly, stats 90d). An explicit `MonitoringSettings` override on the sensor/its ancestors always wins; the profile is only the fallback. The ElementEditor surfaces this as a visible **Telemetry & retention** section that shows the active profile and uses it for the field placeholders.
- **Poll cadence is per-sensor-type by default too.** `Matmon.Core/Domain/SensorScheduleDefaults` maps each type to a fallback poll interval (ping 30s, http/snmp 60s, ntp 5m, ssl/cert 6h, backup-job 30m, disk-smart 15m, …; `Default` 60s). `SensorPollingService` uses it as the `IsDue` fallback so a sensor with no explicit `PollingInterval` polls at a sane type-specific rate instead of a flat 15s. An explicit schedule on the sensor/its ancestors still wins.
- **Statistics are accurate, downsampled aggregates — not a running average.** `SensorStatisticsBucket` carries avg/min/max, the low/high percentile (bottom 1% / top 1%) and a healthy/warning/critical distribution (→ uptime %). `Matmon.Core/Telemetry/TelemetryRollup` (pure, unit-tested) computes a bucket from the raw observations inside its window via linear-interpolation percentiles. `StatisticsRollupService` (Primary-only, every 5 min) calls `IMonitoringWorkspaceStore.RunTelemetryMaintenance`, which recomputes the recent buckets per sensor from raw data **and** applies all telemetry retention (raw/statistics/events). Pruning and aggregation are therefore **off the polling hot path** — `RecordSensorObservation` only appends the raw observation, its state-change event and the alert sync.
- **Telemetry now lives in SQLite (Phase A done).** `Matmon.Core/Telemetry` holds `ITelemetryRepository` (storage contract for observations/events/statistics) and the embedded `SqliteTelemetryRepository` (WAL, `Microsoft.Data.Sqlite`). `InMemoryMonitoringWorkspaceStore` delegates all telemetry storage/queries/prune/cleanup to it; the `SensorHistory`/`Events`/`SensorStatistics` collections on `WorkspaceDocument` are kept only as a backup/restore transport and are **always empty in `workspace.json`** (cleared on load). On startup `MigrateDocumentTelemetryIntoRepository` moves any telemetry found in an existing `workspace.json` into `telemetry.db` once (only when the DB is empty), then drops it from the JSON. DB path: `data/telemetry.db` (override `Matmon__TelemetryPath`). The former save bottleneck is gone — telemetry saves no longer re-serialize history. Backup create pulls the selected telemetry sections from the repo into the snapshot; restore pushes them back via `ReplaceAll*`. **Perf:** the repo keeps an in-memory latest-observation-per-sensor cache (`_latestBySensor`), so `GetLatestObservations`/`GetLatestObservation` (dashboard + polling hot paths) never scan the table; WAL is checkpoint-truncated on startup and after bulk imports, with `wal_autocheckpoint` on. Without this the dashboard did a full-table window scan per load (~15s on a real DB).

## Auth & authorization

Cookie auth (`Program.cs`). Roles `Admin` / `User` (`MatmonUserRole`). Admin-only pages are declared with `AuthorizePage(..., MatmonSecurity.AdminPolicy)` conventions; a global `MatmonPageWriteGuard` MVC filter enforces write protection. API paths return 401/403 (JSON) instead of redirecting. Passwords hashed via `MatmonPasswordHasher`. Credentials encrypted at rest via ASP.NET DataProtection (`HydrateCredentialBundles` / `ProtectCredentialBundles`).

## Configuration (env vars)

All under the `Matmon__` prefix (see `appsettings.json` + README). Common: `Matmon__Mode`, `Matmon__WorkspacePath`, `Matmon__Auth__Username|Password`, `Matmon__HeartbeatIntervalSeconds`. Seeding/provisioning flags default to false: `SeedSampleData`, `ProvisionLocalDockerProbe`, `ProvisionDemoSensors`, `AutoCreateProbeSystemSensors`, `CreateStarterMap`. Secondary: `ProbeId`, `ProbeName`, `ProbeToken`, `PrimaryUrl`.

## Docker

`Dockerfile` at `src/Matmon.Host/Dockerfile` (SDK build → aspnet runtime; installs PowerShell + ssh + NTLM for sensors; binds `:8099`; takes a `MATMON_VERSION` build-arg → env). Compose files: `docker-compose.yml` (local primary + sample probe), `docker-compose.master*.yml` (portable primary, GHCR pull / host-network / local build). CI: `.github/workflows/docker-image.yml` builds on PR, publishes to GHCR on push to `main`/`dev`.

## Branches & versioning

- **`main`** = release branch (tagged `latest`); **`dev`** = ongoing work (tagged `nightly`). Day-to-day work happens on `dev`; merge to `main` for releases.
- The build version is shown in the UI (sidebar footer "Matmon &lt;version&gt;", links to `/About`). It is resolved by `Services/MatmonVersion` from the `MATMON_VERSION` env var, which CI bakes into the image: **release** `0.1.<run>-<builddate>` (base from the root `VERSION` file), **dev** `nightly-<run>-<builddate>`. With no env var (plain local/compose build) it falls back to `local-<builddate>` from the assembly timestamp. `builddate` = UTC `yyyyMMdd-HHmm`.

## Conventions

- C# nullable + implicit usings on; modern C# (collection expressions `[]`, records, pattern matching). Keep new code matching the surrounding style.
- Razor Pages, one page = `Xxx.cshtml` + `Xxx.cshtml.cs`. Create/Edit dialogs are the `*Create` / `*Edit` / `*Editor` pages. The sensor editors (SensorCreate, SensorAssistant, ElementEditor, TemplateEditor) share two partials to avoid duplication: `Pages/Shared/_SensorThresholds.cshtml` (`@model ISensorThresholdEditor`) and `_SensorSchedule.cshtml` (`@model ISensorScheduleEditor`). Each input model in `Workspace.cshtml.cs` implements those interfaces; render with `Html.PartialAsync(..., new ViewDataDictionary(ViewData){ TemplateInfo = { HtmlFieldPrefix = "<Prefix>" } })`. The schedule editor is mode-based (inherit/every/daily/weekly/monthly) with value+unit intervals and a JS live "next runs" preview (`initializeScheduleEditors` in site.js).
- **Tabs are app-wide one style.** The canonical tab UI is `.sensor-tabs` / `.sensor-tab` (clean underline, no panel frame) driven by `data-sensor-tabs` (bar) + `data-sensor-tab-target` (button) + `data-sensor-tab="<name>"` (panel); `initializeSensorTabs` in site.js toggles panels scoped to the bar's `closest("form")`. Used by SensorCreate/SensorAssistant, ElementEditor, TemplateEditor (edit mode), Config/System and SensorDetails. The shared `_SensorThresholds`/`_SensorSchedule` partials carry their own `data-sensor-tab` so any page rendering them gets the Thresholds/Schedule tab for free. Don't reintroduce the old boxed `.workspace-tabs` style.
- **Sensor Create/Assistant "preview" re-post recomputes server-side state** (suggested name via `EnsureSuggestedSensorName`, applied template defaults, rebuilt channel/parameter fields). `OnPostPreviewSensorFields` and `OnPostDiscoverSnmp` must `ModelState.Clear()` before `return Page()` — otherwise the `asp-for` tag helpers re-render bound inputs from the posted ModelState and mask the recomputed values (the prefilled name would stay e.g. "Ping 3" after switching the type to HTTP).
- All workspace mutations go through `IMonitoringWorkspaceStore` and must run under its `_gate`; they call `QueueSave(SavePriority.*)`.
- `DashboardSnapshotProvider.CreateSnapshot()` is **heavy** — it clones the workspace twice, builds a telemetry series (graph points) for every sensor and calls `SyncAlerts` (a GET-time mutation). Use it only on the dashboard (`/Index`) and `/api/dashboard`. The shared `_Layout` summary strip uses the cheap `GetWorkspaceSummary()` (topology counts only); never put `CreateSnapshot()` on a path that runs for every page.
- **Alerts are Alerta-style: they persist until acknowledged.** `MonitoringAlert` has `AcknowledgedUtc`, `RecoveredUtc` and `ResolvedUtc` (`IsActive = ResolvedUtc is null`). Recovery does **not** silently close an unacknowledged alert: `MarkAlertsRecoveredForElement` (poll path) and the recovery branch of `SyncAlerts` (dashboard) only set `ResolvedUtc` when the alert is already acknowledged; otherwise they set `RecoveredUtc` and keep it active (shown in the Open list with a "Recovered" pill). Acknowledging a recovered alert resolves it (`AcknowledgeAlert`). A re-alarm clears `RecoveredUtc`. Pausing a sensor still resolves outright via `ResolveAlertsForElement`.
- Keep the build green (`dotnet build Matmon.slnx`) after each change.

## Known gaps / cleanup targets

- **Thin test coverage** — `Matmon.Tests` now exists with a baseline for core pure logic; the tree-level `MonitoringInheritanceResolver`, telemetry retention/migration and the executors are still uncovered.
- `InMemoryMonitoringWorkspaceStore` is a god-class mixing many concerns; decompose into focused services.
- JSON-everything persistence does not scale (see above) — SQLite telemetry migration is the planned fix.
