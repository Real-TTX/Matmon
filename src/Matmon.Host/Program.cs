using System.Security.Claims;
using System.IO;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var runtimeOptions = builder.Configuration.GetSection("Matmon").Get<MatmonRuntimeOptions>() ?? new MatmonRuntimeOptions();
runtimeOptions.ProbeId ??= Environment.MachineName;
runtimeOptions.ProbeName ??= Environment.MachineName;

var authOptions = builder.Configuration.GetSection("Matmon:Auth").Get<MatmonAuthOptions>() ?? new MatmonAuthOptions();
authOptions.Username = string.IsNullOrWhiteSpace(authOptions.Username) ? "admin" : authOptions.Username;
authOptions.Password = string.IsNullOrWhiteSpace(authOptions.Password) ? "admin" : authOptions.Password;

var workspacePath = string.IsNullOrWhiteSpace(runtimeOptions.WorkspacePath)
    ? "data/workspace.json"
    : runtimeOptions.WorkspacePath;
var resolvedWorkspacePath = Path.IsPathRooted(workspacePath)
    ? workspacePath
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, workspacePath));
var workspaceDirectory = Path.GetDirectoryName(resolvedWorkspacePath)
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var dataProtectionDirectory = Path.Combine(workspaceDirectory, "dataprotection-keys");
Directory.CreateDirectory(dataProtectionDirectory);

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
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".Matmon.Auth";
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
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
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

RegisterSensorExecutors(builder.Services);
builder.Services.AddScoped<ISensorExecutionService, SensorExecutionService>();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<IDashboardSnapshotProvider, DashboardSnapshotProvider>();

if (runtimeOptions.Mode == AppMode.Master)
{
    builder.Services.AddHostedService<SensorPollingService>();
}
else
{
    builder.Services.AddHostedService<SlaveHeartbeatService>();
    builder.Services.AddHostedService<SlaveSensorWorker>();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment() && runtimeOptions.Mode == AppMode.Master)
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

if (runtimeOptions.Mode == AppMode.Master)
{
    app.MapGet("/api/dashboard", (IDashboardSnapshotProvider provider) =>
        Results.Ok(provider.CreateSnapshot()));

    app.MapGet("/api/topology", (IDashboardSnapshotProvider provider) =>
        Results.Ok(provider.CreateSnapshot()));

    app.MapGet("/api/probes", (IProbeRegistry registry) =>
        Results.Ok(registry.GetAll()));

    app.MapGet("/api/configuration", (IConfigurationOverviewProvider provider) =>
        Results.Ok(provider.GetOverview()));

    app.MapGet("/api/discovery-jobs/{jobId:guid}", (Guid jobId, DiscoveryJobStore discoveryJobs) =>
    {
        var job = discoveryJobs.Find(jobId);
        if (job is null)
        {
            return Results.NotFound();
        }

        var isComplete = job.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed;
        return Results.Ok(new DiscoveryJobStatusResponse(
            job.JobId,
            job.ProbeName,
            job.Request.Network,
            job.Status.ToString(),
            job.Message ?? string.Empty,
            isComplete,
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
        foreach (var result in batch.Results)
        {
            var changed = result.IsComplete
                ? discoveryJobs.Complete(result.JobId, result.Hosts, result.ErrorMessage)
                : discoveryJobs.AddResults(result.JobId, result.Hosts);
            if (changed)
            {
                recorded++;
            }
        }

        return Results.Ok(new { recorded });
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
    services.AddTransient<ISensorExecutor, SnmpSensorExecutor>();
    services.AddTransient<ISensorExecutor, SynologyNasSensorExecutor>();
    services.AddTransient<ISensorExecutor, ProxmoxPveSensorExecutor>();
    services.AddTransient<ISensorExecutor, PowerShellRemoteSensorExecutor>();
    services.AddTransient<ISensorExecutor, SslCertificateSensorExecutor>();
    services.AddTransient<ISensorExecutor, MssqlSensorExecutor>();
    services.AddTransient<ISensorExecutor, TcpPortSensorExecutor>();
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
