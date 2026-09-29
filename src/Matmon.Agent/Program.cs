using Matmon.Agent;
using Matmon.Core;
using Matmon.Probe;

// The whole agent. Everything it does is what the Docker probe does - pull assignments and on-demand jobs
// from its primary, run them, post the results back, buffer through an outage, beat - because it IS that
// code, via Matmon.Probe. What makes it an agent rather than a container is only how it is hosted.
if (args.Contains("--version"))
{
    // What an install script, a support call or an update check needs, without starting the service.
    Console.WriteLine(MatmonVersion.Current);
    return 0;
}

var configPath = AgentConfigFile.Resolve(args);
if (args.FirstOrDefault() == "enroll")
{
    return await EnrollCommand.RunAsync(args, configPath);
}

if (args.FirstOrDefault() == "apply-update")
{
    return await ApplyUpdateCommand.RunAsync(args);
}

var builder = Host.CreateApplicationBuilder(args);

// The identity enroll wrote. Environment variables and switches are re-added AFTER it so they still win -
// the Docker-probe way of configuring (Matmon__PrimaryUrl, ...) keeps working and can override a field.
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// Both are no-ops unless the process was actually started by the Service Control Manager / systemd, so one
// binary runs as a Windows service, as a systemd unit, and interactively from a terminal.
builder.Services.AddWindowsService(options => options.ServiceName = "Matmon Agent");
builder.Services.AddSystemd();

// The agent polls its primary every few seconds; at Information, HttpClient logs four lines per request,
// which on a customer's event log / journal buries everything the agent itself says. In code rather than in
// an appsettings.json, because a single-file publish does not ship one next to the executable.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Same "Matmon" section and the same Matmon__* environment names as the Docker probe, so a probe config
// moves over unchanged. An agent is always a secondary - there is no mode to get wrong.
var options = builder.Configuration.GetSection("Matmon").Get<ProbeRuntimeOptions>() ?? new ProbeRuntimeOptions();
options.Mode = AppMode.Secondary;
if (string.IsNullOrWhiteSpace(options.PrimaryUrl) || string.IsNullOrWhiteSpace(options.ProbeId) || string.IsNullOrWhiteSpace(options.ProbeToken))
{
    // Without an identity every request would be a 401, forever, in a service log nobody reads. Failing to
    // start says it where it is seen: in the service manager, with the fix in the message.
    Console.Error.WriteLine($"Not enrolled: no primary URL / probe id / probe token in {configPath} or the Matmon__* environment.");
    Console.Error.WriteLine("Run: matmon-agent enroll --url <instance url> --code <code from the Agents page>");
    return 1;
}

builder.Services.AddSingleton(options);

// What "storage" means for an agent: the directory it is installed in and the drive under it. It keeps no
// workspace, telemetry or backups - the primary does - so that is the only disk the agent itself can fill.
// Matmon__DataPath points it elsewhere, e.g. at a data directory an installer created.
var dataPath = builder.Configuration["Matmon:DataPath"];
builder.Services.AddSingleton<IProbeStorageSource>(new DirectoryProbeStorageSource(
    string.IsNullOrWhiteSpace(dataPath) ? AppContext.BaseDirectory : Path.GetFullPath(dataPath)));

builder.Services.AddMatmonSensorExecutors();
builder.Services.AddMatmonProbeHealthSensor();
builder.Services.AddMatmonProbe();

// Auto-update: follow the instance's agent build (see AgentUpdateService). On unless switched off; the check
// interval has a floor so a typo cannot turn it into a download loop.
var autoUpdate = !bool.TryParse(builder.Configuration["Matmon:AgentAutoUpdate"], out var autoUpdateSetting) || autoUpdateSetting;
var checkMinutes = int.TryParse(builder.Configuration["Matmon:AgentUpdateCheckMinutes"], out var configuredMinutes) ? configuredMinutes : 60;
builder.Services.AddSingleton(new AgentUpdateSettings(
    autoUpdate,
    TimeSpan.FromMinutes(Math.Max(5, checkMinutes)),
    AgentConfigFile.ResolveStateDirectory(configPath)));
builder.Services.AddHostedService<AgentUpdateService>();

await builder.Build().RunAsync();
return 0;
