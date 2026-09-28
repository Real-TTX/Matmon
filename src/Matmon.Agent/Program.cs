using Matmon.Core;
using Matmon.Probe;

// The whole agent. Everything it does is what the Docker probe does - pull assignments and on-demand jobs
// from its primary, run them, post the results back, buffer through an outage, beat - because it IS that
// code, via Matmon.Probe. What makes it an agent rather than a container is only how it is hosted.
if (args.Contains("--version"))
{
    // What an install script, a support call or an update check needs, without starting the service.
    Console.WriteLine(MatmonVersion.Current);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Both are no-ops unless the process was actually started by the Service Control Manager / systemd, so one
// binary runs as a Windows service, as a systemd unit, and interactively from a terminal.
builder.Services.AddWindowsService(options => options.ServiceName = "Matmon Agent");
builder.Services.AddSystemd();

// Same "Matmon" section and the same Matmon__* environment names as the Docker probe, so a probe config
// moves over unchanged. An agent is always a secondary - there is no mode to get wrong.
var options = builder.Configuration.GetSection("Matmon").Get<ProbeRuntimeOptions>() ?? new ProbeRuntimeOptions();
options.Mode = AppMode.Secondary;
builder.Services.AddSingleton(options);

builder.Services.AddMatmonSensorExecutors();
builder.Services.AddMatmonProbe();

builder.Build().Run();
