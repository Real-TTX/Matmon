using System.Net;
using System.Text;
using Matmon.Core;
using Matmon.Host.Services;
using Matmon.Host.Services.SelfUpdate;
using Matmon.Host.Ui;
using Matmon.Probe;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Matmon.Tests;

/// <summary>
/// A remote probe installed by the Windows setup: the setup's switches, which role it installs, what goes into the service's
/// environment and the settings file (never the token in the registry), the probe's local status for the tray, and the
/// Windows commands on the probe's Install page.
/// </summary>
public sealed class WindowsProbeInstallTests
{
    private static readonly WindowsInstallationInfo Instance = new(@"C:\Program Files\Matmon", @"C:\ProgramData\Matmon\data", 8099, "1.0");
    private static readonly WindowsInstallationInfo InstalledProbe = Instance with { Role = WindowsInstallationRole.Probe, PrimaryUrl = "https://primary.example" };
    private static readonly WindowsProbeConnection Stored = new("https://primary.example", "probe-01", "stored-token", "Branch office");

    [Fact]
    public void Parses_the_probe_switches()
    {
        var arguments = SetupArguments.Parse(["/silent", "/probe", "/primary=https://primary.example", "/probeid=probe-01", "/token=abc", "/probename=\"Branch office\""]);

        Assert.True(arguments.Silent);
        Assert.True(arguments.WantsProbe);
        Assert.Equal("https://primary.example", arguments.PrimaryUrl);
        Assert.Equal("probe-01", arguments.ProbeId);
        Assert.Equal("abc", arguments.ProbeToken);
        Assert.Equal("Branch office", arguments.ProbeName);
    }

    [Fact]
    public void A_value_alone_asks_for_a_probe_and_an_empty_one_is_none()
    {
        Assert.True(SetupArguments.Parse(["/token=abc"]).WantsProbe);
        Assert.False(SetupArguments.Parse(["/silent"]).WantsProbe);
        Assert.Null(SetupArguments.Parse(["/primary="]).PrimaryUrl);
    }

    [Fact]
    public void A_fresh_probe_needs_all_three_values()
    {
        var (probe, problem) = SetupArguments.Parse(["/probe", "/primary=https://primary.example", "/probeid=probe-01", "/token=abc"]).ResolveProbe(null, null);
        Assert.Null(problem);
        Assert.Equal("abc", probe!.ProbeToken);

        var (missing, why) = SetupArguments.Parse(["/probe", "/primary=https://primary.example", "/probeid=probe-01"]).ResolveProbe(null, null);
        Assert.Null(missing);
        Assert.Contains("token", why);

        var (_, badUrl) = SetupArguments.Parse(["/probe", "/primary=primary.example", "/probeid=probe-01", "/token=abc"]).ResolveProbe(null, null);
        Assert.Contains("http", badUrl);
    }

    [Fact]
    public void One_matmon_per_machine_a_role_change_is_refused()
    {
        var (_, toProbe) = SetupArguments.Parse(["/probe", "/primary=https://p.example", "/probeid=x", "/token=y"]).ResolveProbe(Instance, null);
        Assert.Contains("uninstall the instance first", toProbe);
        Assert.Null(WindowsProbeSettings.RoleChangeProblem(Instance, probe: false));
        Assert.Null(WindowsProbeSettings.RoleChangeProblem(null, probe: true));
    }

    [Fact]
    public void An_update_of_a_probe_keeps_its_stored_connection_and_takes_what_is_new()
    {
        var (kept, problem) = SetupArguments.Parse(["/silent"]).ResolveProbe(InstalledProbe, Stored);
        Assert.Null(problem);
        Assert.Equal(Stored, kept);

        var (changed, _) = SetupArguments.Parse(["/silent", "/token=new-token"]).ResolveProbe(InstalledProbe, Stored);
        Assert.Equal("new-token", changed!.ProbeToken);
        Assert.Equal("probe-01", changed.ProbeId);

        var (unreadable, why) = SetupArguments.Parse(["/silent"]).ResolveProbe(InstalledProbe, null);
        Assert.Null(unreadable);
        Assert.Contains("could not be read", why);
    }

    [Fact]
    public void An_instance_install_has_no_probe()
    {
        Assert.Equal(((WindowsProbeConnection?)null, (string?)null), SetupArguments.Parse(["/silent"]).ResolveProbe(Instance, null));
        Assert.Equal(((WindowsProbeConnection?)null, (string?)null), SetupArguments.Parse(["/silent"]).ResolveProbe(null, null));
    }

    [Fact]
    public void The_service_environment_never_carries_the_token()
    {
        var instance = WindowsProbeSettings.ServiceEnvironment(new WindowsInstallOptions(@"C:\P", @"C:\D", 8100, false));
        Assert.Equal(["ASPNETCORE_URLS=http://+:8100", @"Matmon__WorkspacePath=C:\D\workspace.json"], instance);

        var probe = WindowsProbeSettings.ServiceEnvironment(new WindowsInstallOptions(@"C:\P", @"C:\D", 8100, false, Probe: Stored));
        Assert.Contains($@"{WindowsProbeSettings.SettingsFileVariable}=C:\D\{WindowsProbeSettings.ProbeSettingsFileName}", probe);
        Assert.DoesNotContain(probe, entry => entry.Contains("stored-token", StringComparison.Ordinal) || entry.Contains("ProbeToken", StringComparison.Ordinal));
    }

    [Fact]
    public void The_settings_file_is_the_section_the_host_binds_and_reads_back()
    {
        var json = WindowsProbeSettings.ProbeSettingsJson(Stored with { PrimaryUrl = "https://primary.example/ " });
        Assert.Equal(Stored, WindowsProbeSettings.ParseProbeSettings(json));

        // What Program.cs does with Matmon__SettingsFile: the file as configuration, environment variables after it.
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var options = configuration.GetSection("Matmon").Get<ProbeRuntimeOptions>()!;
        Assert.Equal(AppMode.Secondary, options.Mode);
        Assert.Equal("https://primary.example", options.PrimaryUrl);
        Assert.Equal("probe-01", options.ProbeId);
        Assert.Equal("stored-token", options.ProbeToken);
        Assert.Equal("Branch office", options.ProbeName);

        Assert.Null(WindowsProbeSettings.ParseProbeSettings("{\"Matmon\":{\"PrimaryUrl\":\"https://p\"}}"));
        Assert.Null(WindowsProbeSettings.ParseProbeSettings("not json"));
    }

    [Fact]
    public void The_shown_primary_address_is_scheme_and_host_only()
    {
        Assert.Equal("https://primary.example:8443", WindowsProbeSettings.DisplayAddress("https://user:secret@primary.example:8443/path?token=x"));
        Assert.Null(WindowsProbeSettings.DisplayAddress("ftp://primary.example"));
        Assert.Null(WindowsProbeSettings.DisplayAddress(null));
    }

    [Fact]
    public void The_local_status_carries_what_the_status_page_shows_and_never_the_token()
    {
        var options = new ProbeRuntimeOptions { ProbeId = "probe-01", ProbeName = "Branch", ProbeToken = "secret-token", PrimaryUrl = "https://user:pw@primary.example/x" };
        var heartbeat = DateTimeOffset.UtcNow.AddSeconds(-5);
        var runtime = new SlaveProbeRuntimeSnapshot(true, "ok", heartbeat, null, null, null, 12, 3, true, "ok", [], [], []);

        var status = ProbeLocalStatus.Build(options, runtime, 7, null);

        Assert.Equal("https://primary.example", status.PrimaryUrl);
        Assert.True(status.Connected);
        Assert.Equal(12, status.AssignedSensorCount);
        Assert.Equal(7, status.BufferedObservations);
        Assert.Equal(heartbeat, status.LastHeartbeatUtc);
        var serialized = System.Text.Json.JsonSerializer.Serialize(status);
        Assert.DoesNotContain("secret-token", serialized);
        Assert.DoesNotContain("Token", serialized);
    }

    [Fact]
    public void The_local_status_answers_only_this_machine()
    {
        static HttpContext Context(IPAddress address, string? forwarded = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = address;
            if (forwarded is not null)
            {
                context.Request.Headers["X-Original-For"] = forwarded;
            }

            return context;
        }

        Assert.True(ProbeLocalStatus.IsLocalCaller(Context(IPAddress.Loopback)));
        Assert.True(ProbeLocalStatus.IsLocalCaller(Context(IPAddress.IPv6Loopback)));
        Assert.False(ProbeLocalStatus.IsLocalCaller(Context(IPAddress.Parse("192.168.1.20"))));
        // A LAN client that claimed 127.0.0.1 through X-Forwarded-For (the middleware keeps its original value).
        Assert.False(ProbeLocalStatus.IsLocalCaller(Context(IPAddress.Loopback, "192.168.1.20")));
    }

    [Fact]
    public void The_install_page_command_is_what_the_setup_parses()
    {
        var probe = new SystemProbeOverview(Guid.NewGuid(), "probe-01", "Branch \"office\"", "Secondary", "Waiting", "", null, "tok-123", 0);

        var arguments = ProbeInstallCommandBuilder.WindowsSetupArguments("https://primary.example", probe);
        var parsed = SetupArguments.Parse([.. arguments]);
        Assert.True(parsed.Silent);
        Assert.True(parsed.WantsProbe);
        Assert.Equal("https://primary.example", parsed.PrimaryUrl);
        Assert.Equal("probe-01", parsed.ProbeId);
        Assert.Equal("tok-123", parsed.ProbeToken);
        Assert.Equal("Branch office", parsed.ProbeName);

        var command = ProbeInstallCommandBuilder.BuildWindowsSetupCommand("https://primary.example", probe);
        Assert.StartsWith("MatmonSetup-x64.exe /silent /probe ", command);
        Assert.Contains("/probename=\"Branch office\"", command);

        var powerShell = ProbeInstallCommandBuilder.BuildWindowsPowerShell("https://primary.example", probe, "Nightly");
        Assert.Contains("releases/download/windows-nightly/MatmonSetup-x64-online.exe", powerShell);
        Assert.Contains("-Verb RunAs", powerShell);
        Assert.Contains("'/token=tok-123'", powerShell);
        Assert.Contains("'/probename=\"Branch office\"'", powerShell);
    }

    [Fact]
    public void The_setup_downloads_follow_the_primarys_channel()
    {
        Assert.EndsWith("/windows-latest/MatmonSetup-x64.exe", ProbeInstallCommandBuilder.WindowsSetupDownloads("Release").Full);
        Assert.EndsWith("/windows-nightly/MatmonSetup-x64-online.exe", ProbeInstallCommandBuilder.WindowsSetupDownloads("Nightly").Online);
        // A local build follows no channel: the release setup is offered.
        Assert.EndsWith("/windows-latest/MatmonSetup-x64.exe", ProbeInstallCommandBuilder.WindowsSetupDownloads("Local").Full);
    }
}
