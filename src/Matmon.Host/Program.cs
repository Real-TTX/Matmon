using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Claims;
using System.IO;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Probe.Syslog;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Matmon.Host.Services.SelfUpdate;
using Microsoft.Extensions.Hosting.WindowsServices;

// Commands that are not the web app: the version (the self-update asks a new build for it before swapping), installing
// the Windows service, and the two updaters, which run in a process / container of their own.
if (args is ["--version"])
{
    Console.WriteLine(Matmon.Probe.MatmonVersion.Current);
    return 0;
}

switch (args.FirstOrDefault())
{
    case WindowsServiceInstaller.InstallCommand or WindowsServiceInstaller.UninstallCommand:
        return WindowsServiceInstaller.Run(args);
    case WindowsUpdateApplier.Command:
        return await WindowsUpdateApplier.RunAsync(args);
    case DockerUpdateHelper.Command:
        return await DockerUpdateHelper.RunAsync(args);
    case RecoveryAdmin.Command:
        return await RecoveryAdmin.RunCommandAsync(args);
    case InstanceRestarter.WindowsCommand:
        return InstanceRestarter.RunWindowsCommand(args);
}

// As a Windows service the working directory is System32: the content root (wwwroot, mibs) is the program folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});
builder.Services.AddWindowsService(options => options.ServiceName = WindowsServiceInstaller.ServiceName);

// A settings file (Matmon__SettingsFile): the Windows setup keeps a probe's connection there, in the data folder, because the
// service's registry environment is readable by every local user and the probe token is a credential. Environment variables
// and switches are re-added after it, so they still win (the agent does the same with its agent.json).
if (builder.Configuration["Matmon:SettingsFile"] is { Length: > 0 } settingsFile)
{
    builder.Configuration.AddJsonFile(settingsFile, optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

// QuestPDF Community license (free for small businesses / < $1M revenue) - required before any PDF gen.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var runtimeOptions = builder.Configuration.GetSection("Matmon").Get<MatmonRuntimeOptions>() ?? new MatmonRuntimeOptions();
runtimeOptions.ProbeId ??= Environment.MachineName;
runtimeOptions.ProbeName ??= Environment.MachineName;

// In-app log viewer: tee every log line into a bounded in-memory ring buffer that the admin "Logs" page reads,
// so operators can see recent logs (heartbeat/cloud/sensor errors) in the UI instead of shelling into docker logs.
var logStore = new InMemoryLogStore(2000);
builder.Services.AddSingleton(logStore);
builder.Logging.AddProvider(new RingBufferLoggerProvider(logStore));

// --- Executor run-mode: a stateless sensor-executor service. No workspace, telemetry, auth-cookie, UI or
//     background loops - just the sensor executors behind a token-authed HTTP API that Matmon.Cloud calls to
//     run cloud sensors. Returns early so none of the stateful Primary/Secondary wiring below is touched. ---
if (runtimeOptions.Mode == AppMode.Executor)
{
    builder.Services.AddSingleton(runtimeOptions);
    builder.Services.AddHttpClient();
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    RegisterSensorExecutors(builder.Services, includeProbeSensors: false);
    // A cloud sensor's URL is typed by a customer: the HTTP sensors may only reach the INTERNET from here, never the
    // network the cloud runs in (its database, the cloud itself, a metadata endpoint) - checked on every connection.
    const string NotPublic = "The address does not lead to a public server - cloud sensors only check what is reachable from the internet.";
    builder.Services.AddHttpClient<HttpSensorExecutor>()
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = PublicNetwork.ConnectCallback(NotPublic) });
    // ... and read at most 2 MB of a page for the content checks (an instance reads 8 MB; the executor serves everyone).
    builder.Services.AddHttpClient<HttpAdvancedSensorExecutor>()
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = PublicNetwork.ConnectCallback(NotPublic) })
        .AddTypedClient((http, _) => new HttpAdvancedSensorExecutor(http) { MaxBodyBytes = 2 * 1024 * 1024 });
    // Every other sensor's target (ping, TCP, certificate, DNS ...) is checked before it runs: StatelessSensorRunner
    // refuses one that does not resolve to public addresses only (ExecutorTargetGuard).
    builder.Services.AddSingleton<StatelessSensorRunner>();

    var executorApp = builder.Build();

    executorApp.MapGet("/healthz", () => Results.Ok(new { status = "ok", mode = "Executor" }));
    executorApp.MapGet("/api/mode", () => Results.Ok(new { mode = "Executor" }));

    executorApp.MapGet("/api/sensor-catalog", (HttpRequest request, StatelessSensorRunner runner) =>
    {
        if (!IsExecutorAuthorized(request, runtimeOptions.ExecutorToken))
        {
            return Results.Unauthorized();
        }
        // The executor shares the instance image, so its version is the cloud's reference for "latest released
        // version" - the cloud reads this header to flag instances that are behind (see the update-check feature).
        request.HttpContext.Response.Headers["X-Matmon-Version"] = Matmon.Probe.MatmonVersion.Current;
        return Results.Ok(runner.Catalog);
    });

    executorApp.MapPost("/api/execute", async (ExecuteSensorRequest body, HttpRequest request, StatelessSensorRunner runner, CancellationToken ct) =>
    {
        if (!IsExecutorAuthorized(request, runtimeOptions.ExecutorToken))
        {
            return Results.Unauthorized();
        }

        var result = await runner.ExecuteAsync(body, ct);
        return Results.Ok(result);
    });

    await executorApp.RunAsync();
    return 0;
}

// Auth credentials are intentionally left blank when not provided: a bare install (no
// Matmon__Auth__* env and no appsettings override) has no pre-provisioned admin and falls through
// to the first-run setup wizard. Setting both via env pre-provisions an admin and skips setup.
var authOptions = builder.Configuration.GetSection("Matmon:Auth").Get<MatmonAuthOptions>() ?? new MatmonAuthOptions();

var workspacePath = string.IsNullOrWhiteSpace(runtimeOptions.WorkspacePath)
    ? "data/workspace.json"
    : runtimeOptions.WorkspacePath;
var resolvedWorkspacePath = Path.IsPathRooted(workspacePath)
    ? workspacePath
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, workspacePath));
// A Docker probe keeps its offline buffer beside the rest of its data, so an outage of the primary survives a
// container restart too (Matmon__OfflineBufferPath overrides it).
if (runtimeOptions.Mode == AppMode.Secondary && string.IsNullOrWhiteSpace(runtimeOptions.OfflineBufferPath))
{
    runtimeOptions.OfflineBufferPath = Path.Combine(Path.GetDirectoryName(resolvedWorkspacePath) ?? builder.Environment.ContentRootPath, "offline-observations.jsonl");
}

// Plugins are loaded from the data directory too (Matmon__PluginsPath overrides it) - a plugin is installed by putting
// it there, the same way any software is: whoever can write the data volume can run code anyway.
if (string.IsNullOrWhiteSpace(runtimeOptions.PluginsPath))
{
    runtimeOptions.PluginsPath = Path.Combine(Path.GetDirectoryName(resolvedWorkspacePath) ?? builder.Environment.ContentRootPath, "plugins");
}

var workspaceDirectory = Path.GetDirectoryName(resolvedWorkspacePath)
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var dataProtectionDirectory = ResolveDataProtectionDirectory(builder.Environment, runtimeOptions, workspaceDirectory);
Directory.CreateDirectory(dataProtectionDirectory);
var telemetryDatabasePath = ResolveTelemetryPath(builder.Environment, runtimeOptions, workspaceDirectory);

builder.Services.AddSingleton(runtimeOptions);
// The probe services (Secondary mode) depend on the probe slice only; same instance, so there is one truth.
builder.Services.AddSingleton<ProbeRuntimeOptions>(runtimeOptions);
builder.Services.AddSingleton(authOptions);
builder.Services.AddDataProtection()
    .SetApplicationName("Matmon")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));
// Distinct TempData cookie name so it never collides with Matmon.Cloud's over the Full-Access tunnel (both are
// served on the same cloud origin there): otherwise each app tries to decrypt the other's default-named temp-data
// cookie with the wrong DataProtection key and logs a permanent "temp data cookie could not be loaded" warning.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(
    options => options.Cookie.Name = ".Matmon.TempData");
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // Only a proxy on this machine, the LAN or the Docker network may say who the client is (TrustedProxies) - a
    // header from anybody else would let a caller pick the address the rate limits count.
    var trusted = TrustedProxies.Resolve(runtimeOptions.TrustedProxies, out var ignoredProxies);
    if (ignoredProxies.Count > 0)
    {
        Console.Error.WriteLine($"Matmon__TrustedProxies: ignored {string.Join(", ", ignoredProxies)} (not an address or CIDR range)");
    }

    if (trusted is null)
    {
        return; // "any": the cleared lists mean every sender is trusted
    }

    foreach (var network in trusted)
    {
        options.KnownIPNetworks.Add(network);
    }

    if (trusted.Count == 0)
    {
        // A list that parsed to nothing must not read as "trust everybody", which is what empty lists mean here.
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.1/32"));
    }
});
builder.Services.AddSingleton<ITelemetryRepository>(_ => new SqliteTelemetryRepository(telemetryDatabasePath));
// Notification hand-off queue (no deps â†’ no DI cycle with the store) + SMTP sender. The store enqueues
// alert transitions via INotificationSink; the Primary-only NotificationDispatchService drains + sends.
builder.Services.AddSingleton<NotificationSpooler>();
builder.Services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<NotificationSpooler>());
builder.Services.AddSingleton<INotificationEmailSender, MailKitEmailSender>();
// The rule's sender picks the channel (SMTP, webhook, cloud); the factory builds a delivery, the client sends it once.
builder.Services.AddSingleton<NotificationDeliveryFactory>();
builder.Services.AddSingleton<NotificationTransportClient>();
builder.Services.AddHttpClient(NotificationTransportClient.HttpClientName);
// Browser notifications (Web Push): the instance's VAPID key pair beside the workspace, a client that only ever connects
// to a public address (the endpoint comes from a browser), and the notifier the transport uses.
builder.Services.AddSingleton(provider => new Matmon.Host.Services.WebPush.WebPushKeys(
    provider.GetRequiredService<IDataProtectionProvider>(), Path.Combine(workspaceDirectory, "push-keys.json")));
builder.Services.AddHttpClient<Matmon.Host.Services.WebPush.WebPushClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = Matmon.Host.Services.WebPush.WebPushClient.ConnectToPublicAddressAsync });
builder.Services.AddTransient<Matmon.Host.Services.WebPush.BrowserPushNotifier>();
builder.Services.AddSingleton<SummaryReportDataCollector>();
builder.Services.AddSingleton<AuditReportPdfBuilder>();
builder.Services.AddSingleton<SummaryReportSender>();
builder.Services.AddSingleton<InMemoryProbeRegistry>();
builder.Services.AddSingleton<IProbeRegistry>(sp => sp.GetRequiredService<InMemoryProbeRegistry>());
builder.Services.AddSingleton<IProbeHeartbeatLookup>(sp => sp.GetRequiredService<InMemoryProbeRegistry>());
// Constructed explicitly rather than by convention so the store gets the MapAssetStore and the MibLibrary - it
// needs them to put uploaded map pictures and MIB files into a local backup and to write them back on restore.
builder.Services.AddSingleton<IMonitoringWorkspaceStore>(provider => new InMemoryMonitoringWorkspaceStore(
    provider.GetRequiredService<IHostEnvironment>(),
    provider.GetRequiredService<MatmonRuntimeOptions>(),
    provider.GetRequiredService<MatmonAuthOptions>(),
    provider.GetRequiredService<IDataProtectionProvider>(),
    provider.GetRequiredService<ITelemetryRepository>(),
    provider.GetRequiredService<ILogger<InMemoryMonitoringWorkspaceStore>>(),
    provider.GetService<INotificationSink>(),
    provider.GetRequiredService<MapAssetStore>(),
    provider.GetRequiredService<MibLibrary>(),
    provider.GetService<SensorWakeQueue>(),
    provider.GetService<ISyslogStore>()));
builder.Services.AddSingleton<StorageOverviewProvider>();
// Agent packages: a hand-placed folder wins, else fetched on demand from the agent feed and cached in the data folder.
builder.Services.AddSingleton(provider => new AgentPackageStore(
    provider.GetRequiredService<IHostEnvironment>(),
    provider.GetRequiredService<MatmonRuntimeOptions>(),
    AgentFeedSettings.ForInstance(provider.GetRequiredService<MatmonRuntimeOptions>(), workspaceDirectory),
    provider.GetRequiredService<ILogger<AgentPackageStore>>()));
builder.Services.AddSingleton<AgentInstallerCodes>();
builder.Services.AddSingleton<IProbeStorageSource>(provider => provider.GetRequiredService<StorageOverviewProvider>());
builder.Services.AddSingleton<IConfigurationOverviewProvider, ConfigurationOverviewProvider>();
builder.Services.AddSingleton<SlaveProbeRuntimeState>();
builder.Services.AddSingleton<ProbeSensorAssignmentProvider>();
builder.Services.AddSingleton<NetworkDiscoveryService>();
builder.Services.AddSingleton<DiscoveryJobStore>();
builder.Services.AddSingleton<Matmon.Host.Services.Import.PrtgImportSessions>();
builder.Services.AddSingleton<IOnDemandRunStore, OnDemandRunStore>();
builder.Services.AddSingleton<MapDisplayProvider>();
builder.Services.AddSingleton<StatusPageProvider>();
builder.Services.AddSingleton<MapGenerator>();
// Backups to an SMB share (a NAS): the OS share connection on Windows, Samba's smbclient everywhere else.
builder.Services.AddSingleton<Matmon.Host.Services.Backup.ISmbBackupClient>(_ => OperatingSystem.IsWindows()
    ? new Matmon.Host.Services.Backup.WindowsSmbBackupClient()
    : new Matmon.Host.Services.Backup.SmbclientBackupClient());
builder.Services.AddSingleton<Matmon.Host.Services.Backup.SmbBackupRunner>();
// Uploaded map pictures live on disk under data/uploads, NOT in workspace.json - that file is fully
// re-serialised on a 750ms debounce, so a 1 MB floorplan in it would be rewritten every time a pin moves.
builder.Services.AddSingleton(provider => new MapAssetStore(
    workspaceDirectory,
    provider.GetRequiredService<ILogger<MapAssetStore>>()));
// SNMP MIBs: shipped standard set + admin uploads (data/mibs). Names are resolved on the instance when a walk is
// shown, so probes never need a MIB.
builder.Services.AddSingleton(provider => new MibLibrary(
    workspaceDirectory,
    Path.Combine(AppContext.BaseDirectory, "mibs"),
    provider.GetRequiredService<ILogger<MibLibrary>>()));
// An SNMP walk stays here while its rows are picked - the editor posts only the walk id and the ticked OIDs.
builder.Services.AddSingleton<SnmpWalkCache>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = BuildAuthCookieName(runtimeOptions);
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddTransient<IClaimsTransformation, MatmonClaimsTransformation>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(MatmonSecurity.AdminPolicy, policy =>
        policy.RequireRole(MatmonUserRole.Admin.ToString()));
    options.AddPolicy(MatmonSecurity.AlertOperatorPolicy, policy =>
        policy.RequireRole(MatmonUserRole.Admin.ToString(), MatmonUserRole.User.ToString()));
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

RegisterSensorExecutors(builder.Services, includePrimaryOnlySensors: runtimeOptions.Mode == AppMode.Primary);
builder.Services.AddScoped<ISensorExecutionService, SensorExecutionService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();

// The agent enrolment endpoint is anonymous by nature (the agent has no identity yet - that is what it is
// asking for), so the only thing between a guesser and a probe token is the code's entropy and this: a few
// attempts per client address per minute.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("agent-enroll", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Push sensors: a job calls once per run, a few jobs behind one address are plenty - a flood is refused, not stored.
    options.AddPolicy("push", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // The instance's own API (API tokens): a pipeline makes a handful of calls per run; guessing tokens is refused long before it matters.
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Wizard", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/Config", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/Mibs", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/CloudClaim", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/MapEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/MapGenerate", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/NotificationReportEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/ProbeCreate", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/ProbeInstall", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/UserCreate", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/UserEdit", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/FolderCreate", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/HostCreate", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SensorCreate", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SensorAssistant", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/ElementEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/TemplateEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/NotificationRuleEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/NotificationSenderEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/NotificationReceiverEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/BackupJobEditor", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/BackupRestore", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/BackupSmbFiles", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/Logs", MatmonSecurity.AdminPolicy);
    // The receiver is a system setting. (Reading the log is a tab of Events; its handlers refuse a viewer themselves -
    // device logs can name users and addresses.)
    options.Conventions.AuthorizePage("/SystemSyslog", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SystemSnmpTraps", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SystemPlugins", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SystemUpdates", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SystemApiTokens", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/PrtgImport", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/SystemApiTokenCreate", MatmonSecurity.AdminPolicy);
}).AddMvcOptions(options =>
{
    options.Filters.Add<MatmonPageWriteGuard>();
    options.Filters.Add<RenderedTempDataFilter>();
});
builder.Services.AddSingleton<IDashboardSnapshotProvider, DashboardSnapshotProvider>();
// Per-process secret shared by the Full Access tunnel client (stamps it on replayed requests) and the
// auto-login middleware (trusts the cloud's identity assertion only when this secret accompanies it).
builder.Services.AddSingleton<TunnelAuthSecret>();
// Live Full Access tunnel status (connected/last-error/served), surfaced on System â†’ Cloud. Registered globally
// (the Config page reads it) even though only the Primary's TunnelClient writes it.
builder.Services.AddSingleton<TunnelState>();
builder.Services.AddSingleton<Pending2faCookie>();
builder.Services.AddSingleton<LoginCodeStore>();
// Holds the "newer build available" signal the cloud returns on each heartbeat. Registered globally (the
// layout reads it) even though only the Primary's CloudConnectionService writes it.
builder.Services.AddSingleton<CloudUpdateState>();
// The MatUi shell: the toolkit's sprite + version (read once) and the per-request menu/brand/breadcrumb model.
builder.Services.AddSingleton<Matmon.Host.Ui.MatUiToolkit>();
builder.Services.AddScoped<Matmon.Host.Ui.InstanceShellBuilder>();

// Talks to Matmon.Cloud's backup endpoints (push/list/download/delete + account-scoped restore). Shared by the
// Config Backup tab, the backup scheduler and the setup wizard, so registered globally.
builder.Services.AddSingleton<CloudBackupClient>();

if (runtimeOptions.Mode == AppMode.Primary)
{
    builder.Services.AddHostedService<SensorPollingService>();
    builder.Services.AddHostedService<BackupSchedulerService>();
    builder.Services.AddHostedService<StatisticsRollupService>();
    builder.Services.AddHostedService<NotificationDispatchService>();
    builder.Services.AddHostedService<ReportSchedulerService>();
    builder.Services.AddHostedService<CloudConnectionService>();
    builder.Services.AddHostedService<TunnelClient>();
    // The way back in that never depends on the cloud: a recovery file in the data folder (RecoveryAdmin).
    builder.Services.AddHostedService(provider => new RecoveryAdminService(
        workspaceDirectory, provider.GetRequiredService<IMonitoringWorkspaceStore>(), provider.GetRequiredService<ILogger<RecoveryAdminService>>()));

    // Updates of this installation itself (System > Updates): through the Docker socket when it is mounted, or as a
    // Windows service from the release feed. The first method that applies wins; none = updated by hand.
    builder.Services.AddSingleton<ISelfUpdateTarget>(new DockerSelfUpdateTarget(
        string.IsNullOrWhiteSpace(runtimeOptions.DockerSocketPath) ? DockerEngineClient.DefaultSocketPath : runtimeOptions.DockerSocketPath,
        Matmon.Probe.MatmonVersion.Current, Matmon.Probe.MatmonVersion.EnvironmentVariable));
    builder.Services.AddSingleton<ISelfUpdateTarget>(provider => new WindowsSelfUpdateTarget(
        runtimeOptions.UpdateFeedUrl, workspaceDirectory, provider.GetRequiredService<IHttpClientFactory>()));
    builder.Services.AddSingleton<SelfUpdateService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<SelfUpdateService>());
    builder.Services.AddSingleton<InstanceRestarter>();
    // Update commands for remote Docker probes (Probes page > Update), handed out with their heartbeat answers.
    builder.Services.AddSingleton<ProbeUpdateCommands>();

    // Syslog: the central log of the network's devices. The database is its own file (syslog.db) so a chatty switch
    // can never bloat or lock the sensor history. Receiving is the syslog collector plugin's job (System > Plugins - on
    // the main probe or on a probe near the devices); nothing listens until an admin switches one on.
    var syslogDatabasePath = ResolveSyslogPath(builder.Environment, runtimeOptions, workspaceDirectory);
    builder.Services.AddSingleton<ISyslogStore>(provider =>
        new SyslogStore(syslogDatabasePath, provider.GetRequiredService<ILogger<SyslogStore>>()));
    builder.Services.AddHostedService<SyslogHousekeepingService>();

    // SNMP traps: the trap collector plugin takes them (wherever it runs) and hands them here - the central log, the MIB
    // names and polling the sending device's sensors at once (System > SNMP traps).
    builder.Services.AddSingleton<SnmpTrapReceiverState>();
    builder.Services.AddSingleton<SensorWakeService>();
    builder.Services.AddSingleton<SnmpTrapIngest>();
    // "Poll this sensor now" from the store (dependencies: check the dependency before a dependent alerts, look at the
    // dependents again once it is back) - the store drops the id in the queue, the worker hands it to SensorWakeService.
    builder.Services.AddSingleton<SensorWakeQueue>();
    builder.Services.AddHostedService<SensorWakeWorker>();

    // Plugins: what the plugins on probes (and on the primary itself) report is handled here, by event kind.
    builder.Services.AddSingleton<Matmon.Host.Services.Plugins.IPluginEventHandler, Matmon.Host.Services.Plugins.SyslogPluginEventHandler>();
    builder.Services.AddSingleton<Matmon.Host.Services.Plugins.IPluginEventHandler, Matmon.Host.Services.Plugins.SnmpTrapPluginEventHandler>();
    builder.Services.AddSingleton<Matmon.Host.Services.Plugins.PluginEventDispatcher>();
    builder.Services.AddSingleton<Matmon.Host.Services.Plugins.PluginStatusRegistry>();
    builder.Services.AddHostedService<Matmon.Host.Services.Plugins.PrimaryPluginService>();
}
else
{
    // The same probe wiring Matmon.Agent uses - see ProbeServiceRegistration.
    builder.Services.AddMatmonProbe();

    // A Docker probe can be updated from its primary (Probes page > Update): the instance's own Docker self-update - the
    // helper container, the health wait, the rollback - with the outcome kept beside the offline buffer.
    builder.Services.AddSingleton(new DockerSelfUpdateTarget(
        string.IsNullOrWhiteSpace(runtimeOptions.DockerSocketPath) ? DockerEngineClient.DefaultSocketPath : runtimeOptions.DockerSocketPath,
        Matmon.Probe.MatmonVersion.Current, Matmon.Probe.MatmonVersion.EnvironmentVariable));
    builder.Services.AddSingleton(new ProbeUpdateOutcomeFile(Path.Combine(
        Path.GetDirectoryName(runtimeOptions.OfflineBufferPath) is { Length: > 0 } bufferDirectory ? bufferDirectory : workspaceDirectory,
        "probe-last-update.json")));
    builder.Services.AddSingleton<ProbeSelfUpdateService>();
    builder.Services.AddSingleton<Matmon.Probe.IProbeSelfUpdate>(provider => provider.GetRequiredService<ProbeSelfUpdateService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ProbeSelfUpdateService>());
}

builder.Services.AddMatmonPlugins(runtimeOptions.PluginsPath);
builder.Services.AddSingleton<Matmon.Host.Services.Plugins.PluginUi>();

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Display timezone (#52): render timestamps in the signed-in user's zone â†’ the admin system default â†’ server
// local. Provider reads the store fresh (in-memory) but memoizes per request. Everything is stored UTC.
{
    var tzAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();
    var tzStore = app.Services.GetRequiredService<IMonitoringWorkspaceStore>();
    DisplayTimeZone.SystemDefault = DisplayTimeZone.Resolve(tzStore.GetDisplayTimeZoneId()) ?? TimeZoneInfo.Local;
    DisplayTimeZone.PerRequestZoneProvider = () =>
    {
        var http = tzAccessor.HttpContext;
        const string key = "__matmon_display_tz";
        if (http is not null && http.Items.TryGetValue(key, out var cached))
        {
            return cached as TimeZoneInfo;
        }
        TimeZoneInfo? zone = null;
        if (http?.User is { Identity.IsAuthenticated: true } user &&
            Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid))
        {
            zone = DisplayTimeZone.Resolve(tzStore.FindUser(uid)?.TimeZoneId);
        }
        if (http is not null)
        {
            http.Items[key] = zone;
        }
        return zone; // null â†’ DisplayTimeZone.Current falls back to SystemDefault
    };
}

app.UseForwardedHeaders();

// HTML (Razor page) responses must not be cached: assets are fingerprinted by
// MapStaticAssets (e.g. site.<hash>.js) and stay immutable-cached, but if the browser
// keeps serving a cached HTML page it references the OLD fingerprinted asset names and
// you get stale CSS/JS even after a deploy. Force HTML to always revalidate so every
// navigation picks up the current asset URLs.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType is { } contentType &&
            contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }

        return Task.CompletedTask;
    });

    await next();
});

if (runtimeOptions.Mode == AppMode.Primary)
{
    // First-run guard: until an admin account exists, funnel every page to the setup wizard.
    // Runs before authentication so the setup redirect wins over the login challenge (there is no
    // account to log in with yet). Static assets, health and APIs stay reachable.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "/";
        var isExempt =
            path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/matui/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains('.');
        if (!isExempt)
        {
            var store = context.RequestServices.GetRequiredService<IMonitoringWorkspaceStore>();
            if (store.IsSetupRequired())
            {
                context.Response.Redirect("/setup");
                return;
            }
        }

        await next();
    });
}

if (runtimeOptions.Mode == AppMode.Secondary)
{
    // A probe has no users and no setup (it is operated on its primary), so every page leads to its status page instead
    // of a sign-in nobody could pass. Static assets, health and the APIs stay as they are.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "/";
        var isOwn =
            path.Equals("/probe", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/matui/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/brand/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains('.');
        if (isOwn)
        {
            await next();
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Redirect("/probe");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    });
}

if (!app.Environment.IsDevelopment() && runtimeOptions.Mode == AppMode.Primary)
{
    app.UseExceptionHandler("/Error");
}

app.UseAuthentication();
// Seamless Full Access sign-in: a cloud user opening the console through the tunnel is auto-signed-in from the
// cloud's trusted identity assertion (runs after authentication so it can override the anonymous principal,
// before authorization so the request is treated as signed-in).
app.UseTunnelAutoLogin();
app.UseAuthorization();
app.UseRateLimiter();

app.MapStaticAssets().AllowAnonymous();

// The version is what both updaters wait for before they call an update done.
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    mode = runtimeOptions.Mode.ToString().ToLowerInvariant(),
    version = Matmon.Probe.MatmonVersion.Current,
    run = InstanceRestarter.ProcessRun
})).AllowAnonymous();

app.MapGet("/api/mode", () => Results.Ok(new
{
    mode = runtimeOptions.Mode.ToString().ToLowerInvariant()
})).AllowAnonymous();

if (runtimeOptions.Mode == AppMode.Secondary)
{
    // The probe's status for a program on its own machine (the Windows tray): what /probe shows, as JSON, loopback only.
    app.MapGet("/api/probe/status", (HttpContext http, Matmon.Probe.SlaveProbeRuntimeState runtime, IServiceProvider services) =>
    {
        if (!ProbeLocalStatus.IsLocalCaller(http))
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(ProbeLocalStatus.Build(runtimeOptions, runtime.Snapshot(), runtime.BufferedObservations,
            services.GetService<Matmon.Probe.IProbeSelfUpdate>()?.Report()));
    }).AllowAnonymous();
}

// The managing partner's co-branding logo, served (not inlined) so the prominently-shown logo - sidebar on
// every page + the login card - is browser-cached instead of re-sent with each page. Anonymous (login needs it);
// 404 when there is no partner logo or the customer suppressed branding. ETag drives conditional 304s.
app.MapGet("/api/branding/logo", (HttpContext http, IMonitoringWorkspaceStore store) =>
{
    var logo = store.GetServicePartnerLogo();
    if (logo is null)
    {
        return Results.NotFound();
    }

    // Belt-and-braces alongside the cache-time magic-byte validation: never let a browser sniff the
    // cloud-supplied bytes into something executable on this origin.
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
        "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(logo.Value.Bytes))[..16] + "\"");
    return Results.File(logo.Value.Bytes, logo.Value.ContentType, entityTag: etag);
}).AllowAnonymous();

// An uploaded map picture. Anonymous because the PUBLIC wallboard has to load it, but the id is an opaque
// GUID and nothing enumerates them. Content type comes from the stored bytes' magic bytes - never from the
// upload's own claim - plus nosniff, because these are served same-origin.
app.MapGet("/api/map-assets/{id:guid}", (HttpContext http, Guid id, MapAssetStore assets) =>
{
    if (assets.Find(id) is not { } asset)
    {
        return Results.NotFound();
    }

    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(asset.Bytes, asset.ContentType,
        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(asset.ETag));
}).AllowAnonymous();

// The partner small logo, served as the instance favicon + mobile-header mark (white-label). Same anonymous,
// ETag-cached pattern as the main logo; 404 when there's no small logo / branding is suppressed.
app.MapGet("/api/branding/favicon", (HttpContext http, IMonitoringWorkspaceStore store) =>
{
    var icon = store.GetServicePartnerSmallLogo();
    if (icon is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
        "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(icon.Value.Bytes))[..16] + "\"");
    return Results.File(icon.Value.Bytes, icon.Value.ContentType, entityTag: etag);
}).AllowAnonymous();

// The installed app (PWA). Its name follows a managing partner's white label (the product name), and its square logo
// leads the icons when it is a PNG big enough for a home screen (192 px or more); Matmon's own icons are always there.
// The colours are the light top bar's - an installed app opens with the system's theme, so a manifest that cannot follow
// the theme takes the default one. Anonymous: a browser fetches it without cookies.
app.MapGet("/manifest.webmanifest", (IMonitoringWorkspaceStore store) =>
{
    var productName = store.GetServicePartnerProductName();
    var name = string.IsNullOrWhiteSpace(productName) ? "Matmon" : productName.Trim();
    var icons = new List<object>();
    if (store.GetServicePartnerSmallLogo() is { } partnerIcon && PngSize(partnerIcon.Bytes) is { } size && size.Width >= 192 && size.Width == size.Height)
    {
        icons.Add(new { src = "/api/branding/favicon", sizes = $"{size.Width}x{size.Height}", type = "image/png", purpose = "any" });
    }

    icons.Add(new { src = "/brand/app-icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" });
    icons.Add(new { src = "/brand/app-icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" });
    icons.Add(new { src = "/brand/app-icon-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" });
    icons.Add(new { src = "/favicon.svg", sizes = "any", type = "image/svg+xml", purpose = "any" });
    var shortcutIcon = new[] { new { src = "/brand/app-icon-192.png", sizes = "192x192" } };
    return Results.Json(new
    {
        id = "/",
        name,
        short_name = name.Length <= 12 ? name : name[..12].TrimEnd(),
        description = "Network monitoring - sensors, alerts and maps.",
        start_url = "/",
        scope = "/",
        display = "standalone",
        display_override = new[] { "standalone", "minimal-ui" },
        orientation = "any",
        background_color = "#f5f4f1",
        theme_color = "#f5f4f1",
        categories = new[] { "utilities", "business" },
        icons,
        shortcuts = new[]
        {
            new { name = "Dashboard", short_name = "Dashboard", url = "/", icons = shortcutIcon },
            new { name = "Alerts", short_name = "Alerts", url = "/Alerts", icons = shortcutIcon },
            new { name = "Sensors", short_name = "Sensors", url = "/monitoring", icons = shortcutIcon }
        }
    }, contentType: "application/manifest+json");
}).AllowAnonymous();

// Public, anonymous catalog of the built-in sensor TYPES (metadata only - no secrets, no workspace data):
// key, name, description, category + whether/what credentials it needs. Lets the marketing site (and anyone)
// show "what Matmon can monitor" without logging in. Same shipped list on every instance.
app.MapGet("/api/sensor-types", () => Results.Ok(new
{
    generatedUtc = DateTimeOffset.UtcNow,
    version = Matmon.Probe.MatmonVersion.Current,
    sensorTypes = SensorDefinitionCatalog.BuiltIns
        .Select(definition => new
        {
            key = definition.Key,
            name = definition.DisplayName,
            description = definition.Description,
            category = SensorTypeCategories.Resolve(definition.Key),
            credentialKinds = definition.CredentialKinds.Select(kind => kind.ToString()).ToArray(),
            needsCredentials = definition.CredentialKinds.Count > 0,
            parameterCount = definition.Parameters.Count
        })
        .OrderBy(type => SensorTypeCategories.OrderIndex(type.category))
        .ThenBy(type => type.name, StringComparer.OrdinalIgnoreCase)
        .ToArray()
})).AllowAnonymous();

if (runtimeOptions.Mode == AppMode.Primary)
{
    app.MapGet("/api/dashboard", (IDashboardSnapshotProvider provider) =>
        Results.Ok(provider.CreateSnapshot()));

    // The menu's alert status on every page: the counts only, with the same names as the dashboard snapshot (which
    // clones the workspace twice and builds a graph series per sensor - far too much for a 5 s poll on every page).
    app.MapGet("/api/alerts/summary", (IMonitoringWorkspaceStore store) =>
    {
        var (open, acknowledged, error, warning) = store.GetActiveAlertCounts();
        return Results.Ok(new
        {
            activeAlertCount = open,
            acknowledgedAlertCount = acknowledged,
            errorAlertCount = error,
            warningAlertCount = warning,
            pausedSensorCount = store.CountPausedSensors()
        });
    });

    app.MapGet("/api/topology", (IDashboardSnapshotProvider provider) =>
        Results.Ok(provider.CreateSnapshot()));

    app.MapGet("/api/probes", (IProbeRegistry registry) =>
        Results.Ok(registry.GetAll()))
        .RequireAuthorization(MatmonSecurity.AdminPolicy);

    // Exposes seed admin credentials + probe enrollment tokens - admin-only, not any signed-in user.
    app.MapGet("/api/configuration", (IConfigurationOverviewProvider provider) =>
        Results.Ok(provider.GetOverview()))
        .RequireAuthorization(MatmonSecurity.AdminPolicy);

    app.MapGet("/api/discovery-jobs/{jobId:guid}", (Guid jobId, DiscoveryJobStore discoveryJobs) =>
    {
        var job = discoveryJobs.Find(jobId);
        if (job is null)
        {
            return Results.NotFound();
        }

        var isComplete = job.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled;
        return Results.Ok(new DiscoveryJobStatusResponse(
            job.JobId,
            job.ProbeName,
            job.Request.Network,
            job.Status.ToString(),
            job.Message ?? string.Empty,
            isComplete,
            job.ScannedHosts,
            job.TotalHosts,
            job.ProgressPercent,
            job.Results));
    });

    // UI poll for an on-demand run routed to a remote probe (a "Test" or SNMP-discover preview). Mirrors
    // the accessibility of GET /api/discovery-jobs/{jobId} (authenticated user, no probe token).
    // Names for walked OIDs from the loaded MIBs - used to relabel a walk in place right after a MIB upload and to
    // name the OIDs a remote probe reports (the probe itself knows no MIBs).
    app.MapPost("/api/mibs/translate", (MibTranslateRequest request, MibLibrary mibs) =>
    {
        var items = (request.Items ?? []).Take(5000).Select(item =>
        {
            var translation = mibs.Translate(item.Oid) is { IsObject: true } known ? known : null;
            return new MibTranslateItem(
                item.Oid,
                translation?.Name,
                translation?.Node.Module,
                translation?.Node.Description,
                translation?.Node.Units,
                translation is null ? item.Value : translation.FormatValue(item.Value));
        }).ToList();
        return Results.Ok(new { modules = mibs.Registry.Modules.Count, items });
    });

    app.MapGet("/api/run-jobs/{jobId:guid}", (Guid jobId, IOnDemandRunStore onDemandRunStore) =>
    {
        var job = onDemandRunStore.TryGet(jobId);
        if (job is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new RunJobStatusResponse(
            job.Id,
            job.Status,
            OnDemandRunStatus.IsFinished(job.Status),
            job.Result,
            job.Oids,
            job.Error));
    });

    // Browser notifications: the key a browser subscribes with, and the signed-in user's own devices. JSON with a custom
    // header (a cross-site page cannot send that without a preflight this server never answers), on top of the cookie.
    app.MapGet("/api/browser-push/key", (Matmon.Host.Services.WebPush.WebPushKeys keys) => Results.Ok(new { publicKey = keys.PublicKey }))
        .RequireAuthorization();
    app.MapPost("/api/browser-push/devices", (Matmon.Host.Services.WebPush.BrowserPushSubscription body, HttpContext http, IMonitoringWorkspaceStore workspaceStore) =>
    {
        if (!http.Request.Headers.ContainsKey("X-Matmon-Request") || !MatmonSecurity.TryGetCurrentUserId(http.User, out var userId))
        {
            return Results.BadRequest(new { error = "not_allowed" });
        }

        try
        {
            var device = workspaceStore.RegisterPushDevice(userId, body.Endpoint ?? string.Empty, body.Keys?.P256dh ?? string.Empty, body.Keys?.Auth ?? string.Empty,
                PushDeviceNames.FromUserAgent(http.Request.Headers.UserAgent.ToString()));
            return Results.Ok(new { id = device.Id, name = device.Name });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }).RequireAuthorization();
    app.MapPost("/api/browser-push/devices/remove", (Matmon.Host.Services.WebPush.BrowserPushSubscription body, HttpContext http, IMonitoringWorkspaceStore workspaceStore) =>
    {
        if (!http.Request.Headers.ContainsKey("X-Matmon-Request") || !MatmonSecurity.TryGetCurrentUserId(http.User, out var userId))
        {
            return Results.BadRequest(new { error = "not_allowed" });
        }

        var device = workspaceStore.GetPushDevices(userId).FirstOrDefault(candidate => string.Equals(candidate.Endpoint, body.Endpoint, StringComparison.Ordinal));
        return Results.Ok(new { removed = device is not null && workspaceStore.RemovePushDevice(device.Id, userId) });
    }).RequireAuthorization();

    // A push sensor's address: a backup script or cron job reports in (GET or POST, optional status / value / message
    // in the query, a form or JSON). The token is the only credential - anonymous by design, so a script needs no
    // account - and an unknown token answers like a missing page. A paused sensor takes the call and ignores it.
    app.MapMethods("/api/push/{token}", ["GET", "POST"], async (string token, HttpContext http, IMonitoringWorkspaceStore workspaceStore, PushRegistry registry, ILogger<Program> logger) =>
    {
        if (workspaceStore.FindPushSensor(token) is not { } target)
        {
            return Results.NotFound(new { error = "unknown_push_address" });
        }

        var fields = await PushRequestReader.ReadAsync(http.Request, http.RequestAborted);
        if (PushSensor.ParseStatus(fields.Status) is not { } state)
        {
            return Results.BadRequest(new { error = "bad_status", allowed = "ok, warning, error (or 0, 1, 2)" });
        }

        if (!PushRequestReader.TryParseValue(fields.Value, out var value))
        {
            return Results.BadRequest(new { error = "bad_value", expected = "a number, for example 12.5" });
        }

        if (target.IsPaused)
        {
            return Results.Ok(new { ok = true, paused = true });
        }

        var now = DateTimeOffset.UtcNow;
        var push = new PushSensor.Push(now, state, value, PushSensor.CleanMessage(fields.Message));
        registry.Record(target.SensorId, push);
        workspaceStore.RecordSensorObservation(target.SensorId, PushSensor.FromPush(push), now);
        logger.LogDebug("Push for sensor {Sensor}: {State}", target.Name, state);
        return Results.Ok(new { ok = true });
    }).AllowAnonymous().RequireRateLimiting("push");

    // The instance's own API, for CI/CD and scripts (System > API tokens): a token in "Authorization: Bearer mmt_..." (or
    // X-Matmon-Token) with the scope the call needs. Anonymous to the cookie auth - the token is the credential - and
    // answered in JSON. Today: maintenance windows, so a deploy job can silence the alerts of what it restarts.
    var api = app.MapGroup("/api/v1").AllowAnonymous().RequireRateLimiting("api");
    static IResult Answer(Matmon.Host.Services.Api.ApiAnswer answer) => Results.Json(answer.Body, statusCode: answer.Status);
    static (IResult? Refusal, ApiToken? Token) Authorize(HttpContext http, IMonitoringWorkspaceStore store, string scope)
    {
        var secret = ApiTokenSecret.FromHeaders(http.Request.Headers.Authorization, http.Request.Headers["X-Matmon-Token"]);
        var check = store.CheckApiToken(secret, scope, http.Connection.RemoteIpAddress?.ToString());
        http.Response.Headers.CacheControl = "no-store";
        return check.Result switch
        {
            ApiTokenCheckResult.Ok => (null, check.Token),
            ApiTokenCheckResult.Missing => (Results.Json(new { error = "token_required", message = "Send an API token: Authorization: Bearer mmt_... (System > API tokens)." }, statusCode: 401), null),
            ApiTokenCheckResult.Expired => (Results.Json(new { error = "token_expired", message = $"This token expired {check.Token!.ExpiresUtc:yyyy-MM-dd HH:mm} UTC." }, statusCode: 401), null),
            ApiTokenCheckResult.ScopeMissing => (Results.Json(new { error = "scope_missing", message = $"This token may not do this (it needs \"{scope}\")." }, statusCode: 403), null),
            _ => (Results.Json(new { error = "invalid_token", message = "This token is not valid (unknown or revoked)." }, statusCode: 401), null)
        };
    }

    api.MapGet("/maintenance-windows", (HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, _) = Authorize(http, store, ApiTokenScopes.Maintenance);
        return refusal ?? Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).List(DateTimeOffset.UtcNow));
    });
    api.MapGet("/maintenance-windows/{id:guid}", (Guid id, HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, _) = Authorize(http, store, ApiTokenScopes.Maintenance);
        return refusal ?? Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).Get(id, DateTimeOffset.UtcNow));
    });
    api.MapPost("/maintenance-windows", async (HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, token) = Authorize(http, store, ApiTokenScopes.Maintenance);
        if (refusal is not null)
        {
            return refusal;
        }
        Matmon.Host.Services.Api.MaintenanceApiRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<Matmon.Host.Services.Api.MaintenanceApiRequest>(http.RequestAborted);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Results.Json(new { error = "bad_request", message = "The body is not JSON this API reads (Content-Type: application/json)." }, statusCode: 400);
        }
        return Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).Create(request, $"API token \"{token!.Name}\"", DateTimeOffset.UtcNow));
    });
    api.MapPost("/maintenance-windows/{id:guid}/enable", (Guid id, HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, _) = Authorize(http, store, ApiTokenScopes.Maintenance);
        return refusal ?? Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).SetEnabled(id, true, DateTimeOffset.UtcNow));
    });
    api.MapPost("/maintenance-windows/{id:guid}/disable", (Guid id, HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, _) = Authorize(http, store, ApiTokenScopes.Maintenance);
        return refusal ?? Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).SetEnabled(id, false, DateTimeOffset.UtcNow));
    });
    api.MapPost("/maintenance-windows/{id:guid}/end", (Guid id, HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, _) = Authorize(http, store, ApiTokenScopes.Maintenance);
        return refusal ?? Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).EndNow(id, DateTimeOffset.UtcNow));
    });
    api.MapDelete("/maintenance-windows/{id:guid}", (Guid id, HttpContext http, IMonitoringWorkspaceStore store) =>
    {
        var (refusal, _) = Authorize(http, store, ApiTokenScopes.Maintenance);
        return refusal ?? Answer(new Matmon.Host.Services.Api.MaintenanceApi(store).Delete(id));
    });

    // An agent trades a one-time enrolment code (issued on the Agents page) for a probe of its own. Wrong,
    // used and expired codes all answer the same 400 - which codes once existed is not the caller's business.
    // No licence check: agents are not probes in the licence model.
    app.MapPost("/api/agents/enroll", (AgentEnrollRequest request, IMonitoringWorkspaceStore workspaceStore, ILogger<Program> logger) =>
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest(new { error = "code_required" });
        }

        var result = workspaceStore.RedeemAgentEnrollment(request.Code, request.HostName, request.OperatingSystem);
        if (result.Redemption is not { } redemption)
        {
            logger.LogWarning("Agent enrolment rejected: invalid, used or expired code (host {Host})", request.HostName);
            return Results.BadRequest(new { error = "invalid_code" });
        }

        logger.LogInformation(
            "Agent {Host} ({OperatingSystem}, {Version}) {Action} probe {ProbeName} ({ProbeId})",
            request.HostName, request.OperatingSystem, request.AgentVersion,
            redemption.Reenrolled ? "took over" : "enrolled as", redemption.ProbeName, redemption.ProbeId);
        return Results.Ok(new AgentEnrollResponse(redemption.ProbeId, redemption.ProbeToken, redemption.ProbeName));
    }).AllowAnonymous().RequireRateLimiting("agent-enroll");

    // The agent downloads: the manifest and plain packages (fetched on demand from the agent feed - AgentPackageStore),
    // the ready-to-install download with the instance address and a code appended, and the one-liner install scripts.
    app.MapAgentDownloadEndpoints();

    app.MapPost("/api/probes/heartbeat", (ProbeHeartbeatRequest request, IProbeRegistry registry, IMonitoringWorkspaceStore workspaceStore, ProbeUpdateCommands updateCommands) =>
    {
        if (string.IsNullOrWhiteSpace(request.ProbeId) || string.IsNullOrWhiteSpace(request.ProbeName))
        {
            return Results.BadRequest(new { error = "probe id and name are required" });
        }

        if (!workspaceStore.TryValidateProbe(request.ProbeId, request.ProbeToken))
        {
            return Results.Unauthorized();
        }

        registry.Record(request, DateTimeOffset.UtcNow);
        // The answer carries the primary's version and an update command for a Docker probe (Probes page > Update); an
        // older probe ignores the body, so this is backward compatible both ways.
        var command = updateCommands.OnHeartbeat(request.ProbeId, request.AgentVersion, request.SelfUpdate);
        return Results.Ok(new ProbeHeartbeatResponse(Matmon.Probe.MatmonVersion.Current, command));
    }).AllowAnonymous();

    app.MapGet("/api/probes/{probeId}/assignments", (
        string probeId,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        ProbeSensorAssignmentProvider assignmentProvider) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        return Results.Ok(assignmentProvider.BuildAssignments(probeId));
    }).AllowAnonymous();

    app.MapPost("/api/probes/{probeId}/observations", (
        string probeId,
        ProbeSensorObservationBatch batch,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        ProbeSensorAssignmentProvider assignmentProvider) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        if (batch.Observations.Count == 0)
        {
            return Results.Ok(new { recorded = 0 });
        }

        // The probe's clock may be off: move its stamps by the difference to ours (ProbeClock). A moved stamp differs
        // from one send to the next by the network time, so a repeat is then recognised within a couple of seconds -
        // a sensor never runs that often - and otherwise only at the exact same millisecond.
        var receivedUtc = DateTimeOffset.UtcNow;
        var clockOffset = ProbeClock.Offset(batch.SentUtc, receivedUtc);
        var duplicateTolerance = clockOffset == TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(2);
        var recorded = 0;
        var repeated = 0;
        foreach (var observation in batch.Observations)
        {
            if (!assignmentProvider.TryBuildRecordingContext(
                    probeId,
                    observation.SensorId,
                    out var probe,
                    out _,
                    out var settings))
            {
                continue;
            }

            if (workspaceStore.RecordProbeObservation(
                    observation.SensorId,
                    observation.Result,
                    ProbeClock.Correct(observation.TimestampUtc, clockOffset, receivedUtc),
                    duplicateTolerance,
                    settings,
                    probe.ProbeId,
                    probe.Name))
            {
                recorded++;
            }
            else
            {
                repeated++;
            }
        }

        return Results.Ok(new { recorded, repeated });
    }).AllowAnonymous();

    app.MapGet("/api/probes/{probeId}/discovery-jobs", (
        string probeId,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        DiscoveryJobStore discoveryJobs) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        return Results.Ok(discoveryJobs.TakePendingAssignments(probeId));
    }).AllowAnonymous();

    app.MapPost("/api/probes/{probeId}/discovery-jobs/results", (
        string probeId,
        ProbeDiscoveryJobResultBatch batch,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        DiscoveryJobStore discoveryJobs) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        var recorded = 0;
        var cancelled = false;
        foreach (var result in batch.Results)
        {
            // Only the probe the scan was handed to may report on it.
            if (discoveryJobs.Find(result.JobId) is not { } owned || !string.Equals(owned.ProbeId, probeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (discoveryJobs.IsCancelled(result.JobId))
            {
                cancelled = true;
                continue;
            }

            var changed = false;
            if (result.ScannedHosts.HasValue || result.TotalHosts.HasValue)
            {
                changed = discoveryJobs.UpdateProgress(
                    result.JobId,
                    result.ScannedHosts ?? 0,
                    result.TotalHosts ?? 0);
            }

            changed = result.IsComplete
                ? discoveryJobs.Complete(result.JobId, result.Hosts, result.ErrorMessage) || changed
                : result.Hosts.Count > 0
                    ? discoveryJobs.AddResults(result.JobId, result.Hosts) || changed
                    : changed;
            if (changed)
            {
                recorded++;
            }

            if (discoveryJobs.IsCancelled(result.JobId))
            {
                cancelled = true;
            }
        }

        return Results.Ok(new ProbeDiscoveryJobResultPostResponse(recorded, cancelled));
    }).AllowAnonymous();

    // A remote probe pulls its queued on-demand runs here (Pending -> Running), same token auth as the
    // sensor-assignment/discovery endpoints.
    // --- Plugins on probes: what to run, and what they saw (events, with their real times, after an outage too) ---------
    app.MapGet("/api/probes/{probeId}/plugins", (string probeId, HttpRequest request, IMonitoringWorkspaceStore workspaceStore) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        var assignments = workspaceStore.GetPluginAssignmentsForProbe(probeId)
            .Select(assignment => new Matmon.Probe.Plugins.ProbePluginAssignment(
                assignment.Id, assignment.PluginId, assignment.Enabled, new Dictionary<string, string>(assignment.Settings)))
            .ToList();
        return Results.Ok(new Matmon.Probe.Plugins.ProbePluginAssignmentsResponse(assignments));
    }).AllowAnonymous();

    app.MapPost("/api/probes/{probeId}/plugins/events", (
        string probeId,
        Matmon.Probe.Plugins.ProbePluginUpload upload,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        Matmon.Host.Services.Plugins.PluginEventDispatcher dispatcher,
        Matmon.Host.Services.Plugins.PluginStatusRegistry registry) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        var events = upload.Events ?? [];
        if (events.Count > 2000)
        {
            return Results.BadRequest(new { error = "at most 2000 events per request" });
        }

        // A log line is stamped with the time it reached the probe - by the probe's clock, so corrected like a result.
        var receivedUtc = DateTimeOffset.UtcNow;
        var clockOffset = ProbeClock.Offset(upload.SentUtc, receivedUtc);
        var repeated = 0;
        foreach (var pluginEvent in events)
        {
            if (!dispatcher.Dispatch(pluginEvent with { OccurredUtc = ProbeClock.Correct(pluginEvent.OccurredUtc, clockOffset, receivedUtc) }, probeId))
            {
                repeated++;
            }
        }

        // A probe speaks for its own assignments only - never for another probe's.
        var now = DateTimeOffset.UtcNow;
        var own = workspaceStore.GetPluginAssignmentsForProbe(probeId).Select(assignment => assignment.Id).ToHashSet();
        foreach (var status in (upload.Statuses ?? []).Where(status => own.Contains(status.AssignmentId)))
        {
            registry.ReportAssignment(status.AssignmentId, new Matmon.Probe.Plugins.PluginStatus(status.State, status.Detail), now);
        }

        registry.ReportProbe(probeId, upload.AvailablePlugins ?? [], upload.QueuedEvents, upload.DroppedEvents, events.Count - repeated, now);
        return Results.Ok(new { accepted = events.Count, repeated });
    }).AllowAnonymous();

    app.MapGet("/api/probes/{probeId}/run-jobs", (
        string probeId,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        IOnDemandRunStore onDemandRunStore) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        var jobs = onDemandRunStore.TakePending(probeId)
            .Select(job => new ProbeRunJobAssignment(
                job.Id,
                job.SensorId,
                job.SensorTypeKey,
                job.Target,
                job.Settings,
                job.RecordObservation,
                job.Kind))
            .ToArray();
        return Results.Ok(new ProbeRunJobAssignmentsResponse(jobs));
    }).AllowAnonymous();

    app.MapPost("/api/probes/{probeId}/run-jobs/results", (
        string probeId,
        ProbeRunJobResultBatch batch,
        HttpRequest request,
        IMonitoringWorkspaceStore workspaceStore,
        ProbeSensorAssignmentProvider assignmentProvider,
        IOnDemandRunStore onDemandRunStore) =>
    {
        if (!workspaceStore.TryValidateProbe(probeId, ReadProbeToken(request)))
        {
            return Results.Unauthorized();
        }

        var receivedUtc = DateTimeOffset.UtcNow;
        var clockOffset = ProbeClock.Offset(batch.SentUtc, receivedUtc);
        var recorded = 0;
        foreach (var result in batch.Results)
        {
            // Complete() returns true only for the transition that actually finishes the job, so recording is
            // idempotent: a duplicate JobId (re-POSTed batch, repeated id) can never write the observation twice.
            var job = onDemandRunStore.TryGet(result.JobId);
            // Only the probe the job was handed to may finish it - another probe's token must not be able to put
            // a made-up result in front of the user (the Test preview / Run now message).
            if (job is null || !string.Equals(job.ProbeId, probeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!onDemandRunStore.Complete(result.JobId, result.Result, result.DiscoveredOids, result.Error))
            {
                continue;
            }

            // Only a run that asked to be recorded becomes a real observation, and only via the same
            // server-side re-resolve path as POST /observations - never trust the settings the probe echoed.
            if (job is { RecordObservation: true, SensorId: Guid sensorId } &&
                result.Result is not null &&
                assignmentProvider.TryBuildRecordingContext(probeId, sensorId, out var probe, out _, out var settings))
            {
                workspaceStore.RecordSensorObservation(
                    sensorId,
                    result.Result,
                    ProbeClock.Correct(result.TimestampUtc, clockOffset, receivedUtc),
                    settings,
                    probe.ProbeId,
                    probe.Name);
                recorded++;
            }
        }

        return Results.Ok(new { recorded });
    }).AllowAnonymous();
}

app.MapRazorPages().WithStaticAssets();

app.Logger.LogInformation("Matmon started in {Mode} mode", runtimeOptions.Mode);
app.Run();
return 0;

// Executor run-mode auth: a shared bearer token in the X-Matmon-Executor-Token header (constant-time compared).
// An unset token locks the endpoints entirely.
static bool IsExecutorAuthorized(HttpRequest request, string? expectedToken)
{
    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        return false;
    }

    var provided = request.Headers["X-Matmon-Executor-Token"].FirstOrDefault();
    return !string.IsNullOrEmpty(provided) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(expectedToken));
}

static void RegisterSensorExecutors(IServiceCollection services, bool includeProbeSensors = true, bool includePrimaryOnlySensors = false)
{
    // Every sensor that lives in Matmon.Core - shared with Matmon.Agent, so an agent can never offer a
    // different set of sensors than the primary that assigns it work.
    services.AddMatmonSensorExecutors();

    // Probe heartbeat/health report the probe's OWN state (need probe infra) and are meaningless as cloud
    // sensors - the stateless Executor mode skips them so its executor set resolves without that plumbing.
    if (includeProbeSensors)
    {
        // Mail Health correlates each run with the previous one (round-trip verification), so it relies on
        // SensorExecutionContext.PreviousObservation - available on the Primary + Secondary probe paths but
        // not in the stateless cloud Executor. Registered here so it is offered on instances/probes only.
        services.AddTransient<ISensorExecutor, MailHealthSensorExecutor>();

        services.AddTransient<ProbeHeartbeatSensorExecutor>();
        services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<ProbeHeartbeatSensorExecutor>());
        services.AddMatmonProbeLocalSensors();
        // Instance self-monitoring, driven by the cloud heartbeat's update signal - not a cloud sensor.
        services.AddTransient<MatmonUpdateSensorExecutor>();
        services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<MatmonUpdateSensorExecutor>());
    }

    // The syslog sensors query the log the PRIMARY's receiver keeps (SensorPlacement.RunsOnPrimaryOnly), so only a
    // primary has the store they need - on a secondary probe or in the stateless Executor they would not resolve.
    if (includePrimaryOnlySensors)
    {
        services.AddTransient<ISensorExecutor, SyslogMessagesSensorExecutor>();
        services.AddTransient<ISensorExecutor, SyslogMatchSensorExecutor>();

        // The push sensor: the pushes arrive at the primary's /api/push/{token}.
        services.TryAddSingleton<PushRegistry>();
        services.AddTransient<ISensorExecutor, PushSensorExecutor>();
    }
}

/// <summary>Width and height of a PNG (from its IHDR chunk), or null when the bytes are not one.</summary>
static (int Width, int Height)? PngSize(byte[] bytes)
{
    ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
    {
        return null;
    }

    return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
}

static string? ReadProbeToken(HttpRequest request)
{
    if (request.Headers.TryGetValue("X-Matmon-Probe-Token", out var tokenValues) &&
        !string.IsNullOrWhiteSpace(tokenValues.FirstOrDefault()))
    {
        return tokenValues.FirstOrDefault();
    }

    return request.Query.TryGetValue("token", out var queryToken)
        ? queryToken.FirstOrDefault()
        : null;
}

static string BuildAuthCookieName(MatmonRuntimeOptions options)
{
    var node = options.Mode == AppMode.Secondary
        ? $"Probe.{SanitizeCookieNamePart(options.ProbeId)}"
        : "Primary";
    return $".Matmon.{node}.Auth";
}

static string SanitizeCookieNamePart(string? value)
{
    var normalized = string.IsNullOrWhiteSpace(value) ? "node" : value.Trim();
    var chars = normalized
        .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
            ? character
            : '-')
        .ToArray();
    var result = new string(chars).Trim('-', '.', '_');
    return string.IsNullOrWhiteSpace(result) ? "node" : result;
}

static string ResolveDataProtectionDirectory(IHostEnvironment environment, MatmonRuntimeOptions options, string workspaceDirectory)
{
    var configuredPath = string.IsNullOrWhiteSpace(options.DataProtectionPath)
        ? Path.Combine(workspaceDirectory, "dataprotection-keys")
        : options.DataProtectionPath;

    return Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
}

static string ResolveSyslogPath(IHostEnvironment environment, MatmonRuntimeOptions options, string workspaceDirectory)
{
    var configuredPath = string.IsNullOrWhiteSpace(options.SyslogPath)
        ? Path.Combine(workspaceDirectory, "syslog.db")
        : options.SyslogPath;

    return Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
}

static string ResolveTelemetryPath(IHostEnvironment environment, MatmonRuntimeOptions options, string workspaceDirectory)
{
    var configuredPath = string.IsNullOrWhiteSpace(options.TelemetryPath)
        ? Path.Combine(workspaceDirectory, "telemetry.db")
        : options.TelemetryPath;

    return Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
}

internal sealed record MibTranslateRequest(IReadOnlyList<MibTranslateRequestItem>? Items);

internal sealed record MibTranslateRequestItem(string Oid, string? Value);

internal sealed record MibTranslateItem(string Oid, string? Name, string? Module, string? Description, string? Units, string? Value);
