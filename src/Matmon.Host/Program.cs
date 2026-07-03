using System.Security.Claims;
using System.IO;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// QuestPDF Community license (free for small businesses / < $1M revenue) — required before any PDF gen.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var runtimeOptions = builder.Configuration.GetSection("Matmon").Get<MatmonRuntimeOptions>() ?? new MatmonRuntimeOptions();
runtimeOptions.ProbeId ??= Environment.MachineName;
runtimeOptions.ProbeName ??= Environment.MachineName;

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
var workspaceDirectory = Path.GetDirectoryName(resolvedWorkspacePath)
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var dataProtectionDirectory = ResolveDataProtectionDirectory(builder.Environment, runtimeOptions, workspaceDirectory);
Directory.CreateDirectory(dataProtectionDirectory);
var telemetryDatabasePath = ResolveTelemetryPath(builder.Environment, runtimeOptions, workspaceDirectory);

builder.Services.AddSingleton(runtimeOptions);
builder.Services.AddSingleton(authOptions);
builder.Services.AddDataProtection()
    .SetApplicationName("Matmon")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));
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
});
builder.Services.AddSingleton<ITelemetryRepository>(_ => new SqliteTelemetryRepository(telemetryDatabasePath));
// Notification hand-off queue (no deps → no DI cycle with the store) + SMTP sender. The store enqueues
// alert transitions via INotificationSink; the Primary-only NotificationDispatchService drains + sends.
builder.Services.AddSingleton<NotificationSpooler>();
builder.Services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<NotificationSpooler>());
builder.Services.AddSingleton<INotificationEmailSender, MailKitEmailSender>();
builder.Services.AddSingleton<SummaryReportDataCollector>();
builder.Services.AddSingleton<AuditReportPdfBuilder>();
builder.Services.AddSingleton<SummaryReportSender>();
builder.Services.AddSingleton<InMemoryProbeRegistry>();
builder.Services.AddSingleton<IProbeRegistry>(sp => sp.GetRequiredService<InMemoryProbeRegistry>());
builder.Services.AddSingleton<IProbeHeartbeatLookup>(sp => sp.GetRequiredService<InMemoryProbeRegistry>());
builder.Services.AddSingleton<IMonitoringWorkspaceStore, InMemoryMonitoringWorkspaceStore>();
builder.Services.AddSingleton<StorageOverviewProvider>();
builder.Services.AddSingleton<IConfigurationOverviewProvider, ConfigurationOverviewProvider>();
builder.Services.AddSingleton<SlaveProbeRuntimeState>();
builder.Services.AddSingleton<ProbeSensorAssignmentProvider>();
builder.Services.AddSingleton<NetworkDiscoveryService>();
builder.Services.AddSingleton<DiscoveryJobStore>();
builder.Services.AddSingleton<MapDisplayProvider>();
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

RegisterSensorExecutors(builder.Services);
builder.Services.AddScoped<ISensorExecutionService, SensorExecutionService>();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Wizard", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/Config", MatmonSecurity.AdminPolicy);
    options.Conventions.AuthorizePage("/MapEditor", MatmonSecurity.AdminPolicy);
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
}).AddMvcOptions(options => options.Filters.Add<MatmonPageWriteGuard>());
builder.Services.AddSingleton<IDashboardSnapshotProvider, DashboardSnapshotProvider>();

if (runtimeOptions.Mode == AppMode.Primary)
{
    builder.Services.AddHostedService<SensorPollingService>();
    builder.Services.AddHostedService<BackupSchedulerService>();
    builder.Services.AddHostedService<StatisticsRollupService>();
    builder.Services.AddHostedService<NotificationDispatchService>();
    builder.Services.AddHostedService<ReportSchedulerService>();
}
else
{
    builder.Services.AddHostedService<SlaveHeartbeatService>();
    builder.Services.AddHostedService<SlaveSensorWorker>();
}

var app = builder.Build();

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
            path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase) ||
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

if (!app.Environment.IsDevelopment() && runtimeOptions.Mode == AppMode.Primary)
{
    app.UseExceptionHandler("/Error");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    mode = runtimeOptions.Mode.ToString().ToLowerInvariant()
})).AllowAnonymous();

app.MapGet("/api/mode", () => Results.Ok(new
{
    mode = runtimeOptions.Mode.ToString().ToLowerInvariant()
})).AllowAnonymous();

if (runtimeOptions.Mode == AppMode.Primary)
{
    app.MapGet("/api/dashboard", (IDashboardSnapshotProvider provider) =>
        Results.Ok(provider.CreateSnapshot()));

    app.MapGet("/api/topology", (IDashboardSnapshotProvider provider) =>
        Results.Ok(provider.CreateSnapshot()));

    app.MapGet("/api/probes", (IProbeRegistry registry) =>
        Results.Ok(registry.GetAll()))
        .RequireAuthorization(MatmonSecurity.AdminPolicy);

    // Exposes seed admin credentials + probe enrollment tokens — admin-only, not any signed-in user.
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

    app.MapPost("/api/probes/heartbeat", (ProbeHeartbeatRequest request, IProbeRegistry registry, IMonitoringWorkspaceStore workspaceStore) =>
    {
        if (string.IsNullOrWhiteSpace(request.ProbeId) || string.IsNullOrWhiteSpace(request.ProbeName))
        {
            return Results.BadRequest(new { error = "probe id and name are required" });
        }

        if (!workspaceStore.TryValidateProbe(request.ProbeId, request.ProbeToken))
        {
            return Results.Unauthorized();
        }

        var snapshot = registry.Record(request, DateTimeOffset.UtcNow);
        return Results.Ok(snapshot);
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

        var recorded = 0;
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

            workspaceStore.RecordSensorObservation(
                observation.SensorId,
                observation.Result,
                observation.TimestampUtc,
                settings,
                probe.ProbeId,
                probe.Name);
            recorded++;
        }

        return Results.Ok(new { recorded });
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
}

app.MapRazorPages().WithStaticAssets();

app.Logger.LogInformation("Matmon started in {Mode} mode", runtimeOptions.Mode);
app.Run();

static void RegisterSensorExecutors(IServiceCollection services)
{
    services.AddTransient<ISensorExecutor, PingSensorExecutor>();
    services.AddHttpClient<HttpSensorExecutor>();
    services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<HttpSensorExecutor>());
    services.AddHttpClient<HttpAdvancedSensorExecutor>();
    services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<HttpAdvancedSensorExecutor>());
    services.AddTransient<ISensorExecutor, SnmpSensorExecutor>();
    services.AddTransient<ISensorExecutor, SynologyNasSensorExecutor>();
    services.AddTransient<ISensorExecutor, SynologyHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, SynologyDiskSensorExecutor>();
    services.AddTransient<ISensorExecutor, SynologyUpdateSensorExecutor>();
    services.AddTransient<ISensorExecutor, SnmpInterfaceSensorExecutor>();
    services.AddTransient<ISensorExecutor, UpsSnmpSensorExecutor>();
    services.AddTransient<ISensorExecutor, ProxmoxPveSensorExecutor>();
    services.AddTransient<ISensorExecutor, ProxmoxHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, ProxmoxNodeHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, ProxmoxDiskSensorExecutor>();
    services.AddTransient<ISensorExecutor, VMwareHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, VMwareHostHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, UnifiHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, PowerShellRemoteSensorExecutor>();
    services.AddTransient<ISensorExecutor, LocalScriptSensorExecutor>();
    services.AddTransient<ISensorExecutor, LocalProgramSensorExecutor>();
    services.AddTransient<ISensorExecutor, WindowsHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, WindowsDiskSensorExecutor>();
    services.AddTransient<ISensorExecutor, WindowsUpdateSensorExecutor>();
    services.AddTransient<ISensorExecutor, WindowsServiceSensorExecutor>();
    services.AddTransient<ISensorExecutor, WindowsProcessSensorExecutor>();
    services.AddTransient<ISensorExecutor, LinuxSshHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor, LinuxDiskSensorExecutor>();
    services.AddTransient<ISensorExecutor, LinuxUpdateSensorExecutor>();
    services.AddTransient<ISensorExecutor, SslCertificateSensorExecutor>();
    services.AddTransient<ISensorExecutor, CertificateChainSensorExecutor>();
    services.AddTransient<ISensorExecutor, MssqlSensorExecutor>();
    services.AddTransient<ISensorExecutor, TcpPortSensorExecutor>();
    services.AddTransient<ISensorExecutor, DnsSensorExecutor>();
    services.AddTransient<ISensorExecutor, NtpSensorExecutor>();
    services.AddTransient<ISensorExecutor, DockerContainerSensorExecutor>();
    services.AddTransient<ISensorExecutor, BackupJobSensorExecutor>();
    services.AddTransient<ISensorExecutor, DiskSmartSensorExecutor>();
    services.AddTransient<ProbeHeartbeatSensorExecutor>();
    services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<ProbeHeartbeatSensorExecutor>());
    services.AddTransient<ProbeHealthSensorExecutor>();
    services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<ProbeHealthSensorExecutor>());
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

static string ResolveTelemetryPath(IHostEnvironment environment, MatmonRuntimeOptions options, string workspaceDirectory)
{
    var configuredPath = string.IsNullOrWhiteSpace(options.TelemetryPath)
        ? Path.Combine(workspaceDirectory, "telemetry.db")
        : options.TelemetryPath;

    return Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
}
