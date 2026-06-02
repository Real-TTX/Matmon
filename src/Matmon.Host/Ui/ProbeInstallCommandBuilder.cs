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
        var authUsername = string.IsNullOrWhiteSpace(overview.AuthUsername) ? "admin" : overview.AuthUsername;
        var authPassword = string.IsNullOrWhiteSpace(overview.AuthPassword) ? "admin" : overview.AuthPassword;

        return $"""
docker run -d \
  --name {containerName} \
  --restart unless-stopped \
  -p 8099:8099 \
  -v {volumeName}:/app/data \
  -e ASPNETCORE_URLS=http://+:8099 \
  -e Matmon__Mode=Secondary \
  -e Matmon__ProbeId={probe.ProbeId} \
  -e Matmon__ProbeName={ShellQuote(probe.Name)} \
  -e Matmon__ProbeToken={ShellQuote(token)} \
  -e Matmon__PrimaryUrl={ShellQuote(primaryUrl)} \
  -e Matmon__HeartbeatIntervalSeconds={overview.HeartbeatIntervalSeconds} \
  -e Matmon__WorkspacePath=/app/data/workspace.json \
  -e Matmon__Auth__Username={ShellQuote(authUsername)} \
  -e Matmon__Auth__Password={ShellQuote(authPassword)} \
  {DefaultImage}
""";
    }

    public static string BuildCompose(HttpRequest request, ConfigurationOverview overview, SystemProbeOverview probe)
    {
        var token = probe.EnrollmentToken ?? "token-here";
        var containerName = BuildContainerName(probe);
        var volumeName = $"{containerName}-data";
        var primaryUrl = BuildPrimaryUrl(request, overview.MasterUrl);
        var authUsername = string.IsNullOrWhiteSpace(overview.AuthUsername) ? "admin" : overview.AuthUsername;
        var authPassword = string.IsNullOrWhiteSpace(overview.AuthPassword) ? "admin" : overview.AuthPassword;

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
      Matmon__Auth__Username: {YamlQuote(authUsername)}
      Matmon__Auth__Password: {YamlQuote(authPassword)}
    volumes:
      - matmon-probe-data:/app/data

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
        var authUsername = string.IsNullOrWhiteSpace(overview.AuthUsername) ? "admin" : overview.AuthUsername;
        var authPassword = string.IsNullOrWhiteSpace(overview.AuthPassword) ? "admin" : overview.AuthPassword;

        return $"""
curl -fsSL https://raw.githubusercontent.com/Real-TTX/Matmon/main/scripts/install-probe.sh | \
  MATMON_PROBE_ID={probe.ProbeId} \
  MATMON_PROBE_NAME={ShellQuote(probe.Name)} \
  MATMON_PROBE_TOKEN={ShellQuote(token)} \
  MATMON_PRIMARY_URL={ShellQuote(primaryUrl)} \
  MATMON_CONTAINER_NAME={containerName} \
  MATMON_DATA_VOLUME={volumeName} \
  MATMON_ADMIN_USER={ShellQuote(authUsername)} \
  MATMON_ADMIN_PASSWORD={ShellQuote(authPassword)} \
  sh
""";
    }

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
