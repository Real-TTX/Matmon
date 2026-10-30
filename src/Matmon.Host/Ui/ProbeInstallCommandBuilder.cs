namespace Matmon.Host.Ui;

using Matmon.Host.Services;
using Microsoft.AspNetCore.Http;
using System.Linq;

public static class ProbeInstallCommandBuilder
{
    private const string DefaultImage = "ghcr.io/real-ttx/matmon:latest";

    public static bool CanInstallProbe(SystemProbeOverview probe)
    {
        return !string.Equals(probe.Role, "Primary", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(probe.EnrollmentToken);
    }

    public static string BuildDockerRun(HttpRequest request, ConfigurationOverview overview, SystemProbeOverview probe)
    {
        var token = probe.EnrollmentToken ?? "token-here";
        var containerName = BuildContainerName(probe);
        var volumeName = $"{containerName}-data";
        var primaryUrl = BuildPrimaryUrl(request, overview.MasterUrl);

        return $"""
docker run -d \
  --name {containerName} \
  --restart unless-stopped \
  -p 8099:8099 \
  -v {volumeName}:/app/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e ASPNETCORE_URLS=http://+:8099 \
  -e Matmon__Mode=Secondary \
  -e Matmon__ProbeId={probe.ProbeId} \
  -e Matmon__ProbeName={ShellQuote(probe.Name)} \
  -e Matmon__ProbeToken={ShellQuote(token)} \
  -e Matmon__PrimaryUrl={ShellQuote(primaryUrl)} \
  -e Matmon__HeartbeatIntervalSeconds={overview.HeartbeatIntervalSeconds} \
  -e Matmon__WorkspacePath=/app/data/workspace.json \
  {DefaultImage}
""";
    }

    public static string BuildCompose(HttpRequest request, ConfigurationOverview overview, SystemProbeOverview probe)
    {
        var token = probe.EnrollmentToken ?? "token-here";
        var containerName = BuildContainerName(probe);
        var volumeName = $"{containerName}-data";
        var primaryUrl = BuildPrimaryUrl(request, overview.MasterUrl);

        return $"""
services:
  matmon-probe:
    image: {DefaultImage}
    pull_policy: always
    container_name: {containerName}
    restart: unless-stopped
    ports:
      - "8099:8099"
    environment:
      ASPNETCORE_URLS: http://+:8099
      Matmon__Mode: Secondary
      Matmon__ProbeId: {probe.ProbeId}
      Matmon__ProbeName: {YamlQuote(probe.Name)}
      Matmon__ProbeToken: {YamlQuote(token)}
      Matmon__PrimaryUrl: {YamlQuote(primaryUrl)}
      Matmon__HeartbeatIntervalSeconds: {overview.HeartbeatIntervalSeconds}
      Matmon__WorkspacePath: /app/data/workspace.json
    volumes:
      - matmon-probe-data:/app/data
      # lets the primary update this probe (Probes > Update); gives the container control over Docker here
      - /var/run/docker.sock:/var/run/docker.sock

volumes:
  matmon-probe-data:
    name: {volumeName}
""";
    }

    public static string BuildCurlInstaller(HttpRequest request, ConfigurationOverview overview, SystemProbeOverview probe)
    {
        var token = probe.EnrollmentToken ?? "token-here";
        var containerName = BuildContainerName(probe);
        var volumeName = $"{containerName}-data";
        var primaryUrl = BuildPrimaryUrl(request, overview.MasterUrl);

        return $"""
curl -fsSL https://raw.githubusercontent.com/Real-TTX/Matmon/main/scripts/install-probe.sh | \
  MATMON_PROBE_ID={probe.ProbeId} \
  MATMON_PROBE_NAME={ShellQuote(probe.Name)} \
  MATMON_PROBE_TOKEN={ShellQuote(token)} \
  MATMON_PRIMARY_URL={ShellQuote(primaryUrl)} \
  MATMON_CONTAINER_NAME={containerName} \
  MATMON_DATA_VOLUME={volumeName} \
  sh
""";
    }

    /// <summary>The Windows setup of the primary's own channel (the rolling GitHub release beside the self-update's package):
    /// the full setup and the online one, which downloads the package while it installs.</summary>
    public static (string Full, string Online) WindowsSetupDownloads(string? channel = null)
    {
        var feed = Matmon.Host.Services.SelfUpdate.WindowsUpdateFeed.FeedUrl(null, channel ?? Matmon.Probe.MatmonVersion.Channel)
            ?? Matmon.Host.Services.SelfUpdate.WindowsUpdateFeed.FeedUrl(null, "Release")!;
        return ($"{feed}/MatmonSetup-x64.exe", $"{feed}/MatmonSetup-x64-online.exe");
    }

    /// <summary>The setup's silent switches for this probe - for a setup that is already downloaded (administrator prompt).</summary>
    public static string BuildWindowsSetupCommand(HttpRequest request, ConfigurationOverview overview, SystemProbeOverview probe) =>
        BuildWindowsSetupCommand(PrimaryUrl(request, overview), probe);

    public static string BuildWindowsSetupCommand(string primaryUrl, SystemProbeOverview probe) =>
        "MatmonSetup-x64.exe " + string.Join(' ', WindowsSetupArguments(primaryUrl, probe).Select(argument => argument.Contains(' ') ? CmdQuoteValue(argument) : argument));

    /// <summary>One line for an administrator PowerShell: download the online setup and install the probe silently.</summary>
    public static string BuildWindowsPowerShell(HttpRequest request, ConfigurationOverview overview, SystemProbeOverview probe) =>
        BuildWindowsPowerShell(PrimaryUrl(request, overview), probe);

    public static string BuildWindowsPowerShell(string primaryUrl, SystemProbeOverview probe, string? channel = null)
    {
        var arguments = string.Join(",", WindowsSetupArguments(primaryUrl, probe)
            .Select(argument => "'" + (argument.Contains(' ') ? CmdQuoteValue(argument) : argument).Replace("'", "''", StringComparison.Ordinal) + "'"));
        return $"$s = \"$env:TEMP\\MatmonSetup-x64-online.exe\"; Invoke-WebRequest '{WindowsSetupDownloads(channel).Online}' -OutFile $s -UseBasicParsing; " +
               $"Start-Process $s -Verb RunAs -Wait -ArgumentList {arguments}";
    }

    /// <summary><c>/silent /probe /primary= /probeid= /token= /probename=</c> - see Matmon.Setup's SetupArguments.</summary>
    public static IReadOnlyList<string> WindowsSetupArguments(string primaryUrl, SystemProbeOverview probe)
    {
        // Quotes would end the value early on the setup's command line; a probe name or id never needs them.
        // A trailing backslash would escape the closing quote of a quoted value.
        static string Clean(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal).Trim().TrimEnd('\\');
        return
        [
            "/silent",
            "/probe",
            $"/primary={Clean(primaryUrl)}",
            $"/probeid={Clean(probe.ProbeId)}",
            $"/token={Clean(probe.EnrollmentToken ?? "token-here")}",
            $"/probename={Clean(probe.Name)}"
        ];
    }

    // /name=value with a space in the value: /name="value" (CommandLineToArgvW keeps it one argument, the setup trims the quotes).
    private static string CmdQuoteValue(string argument)
    {
        var equals = argument.IndexOf('=');
        return equals < 0 ? $"\"{argument}\"" : $"{argument[..(equals + 1)]}\"{argument[(equals + 1)..]}\"";
    }

    /// <summary>The address a probe should report to: the configured primary URL, else the one this page was opened at.</summary>
    public static string PrimaryUrl(HttpRequest request, ConfigurationOverview overview) => BuildPrimaryUrl(request, overview.MasterUrl);

    private static string BuildPrimaryUrl(HttpRequest request, string? configuredPrimaryUrl)
    {
        if (!string.IsNullOrWhiteSpace(configuredPrimaryUrl))
        {
            return configuredPrimaryUrl;
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}";
    }

    private static string BuildContainerName(SystemProbeOverview probe)
    {
        var suffix = new string(probe.ProbeId
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrWhiteSpace(suffix)
            ? "matmon-probe"
            : $"matmon-probe-{suffix}";
    }

    private static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string YamlQuote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
