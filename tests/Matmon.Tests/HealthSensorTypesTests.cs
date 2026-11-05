using Matmon.Core.Domain;
using Matmon.Host.Services;
using Xunit.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The health sensors for databases (SQL Server, PostgreSQL, MySQL / MariaDB) and Windows server roles (Exchange, Active Directory,
/// Azure DevOps Server): each is a sensor type of its own, in the catalogue with its category, schedule and default thresholds, its
/// definition carries the connection / WinRM fields but no free query or script. Gated:
/// <c>MATMON_DB_HEALTH_MSSQL / _POSTGRES / _MYSQL / _MARIADB = host:port|user|password</c> runs a database sensor against a real
/// server, <c>MATMON_ROLE_HEALTH_LOCAL=1</c> runs the Windows role sensors through the PowerShell engine on this machine - and every
/// default threshold must name a channel the sensor reports.
/// </summary>
public sealed class HealthSensorTypesTests(ITestOutputHelper output)
{
    public static readonly string[] Types = ["mssql-health", "postgres-health", "mysql-health", "exchange-health", "ad-health", "azure-devops-health"];

    [Fact]
    public void Each_is_a_sensor_type_of_its_own_with_defaults()
    {
        foreach (var type in Types)
        {
            var definition = Assert.Single(SensorDefinitionCatalog.BuiltIns, d => d.Key == type);
            Assert.DoesNotContain(definition.Parameters, parameter => parameter.Key is "query" or "script" or "defaultChannelKey");
            Assert.NotEqual("Other", SensorTypeCategories.Resolve(type));
            var settings = new MonitoringSettings();
            SensorThresholdDefaults.Apply(type, settings);
            Assert.NotEmpty(settings.Thresholds);
        }

        Assert.Contains(MssqlHealthSensorExecutor.Definition.Parameters, p => p.CredentialKind == MonitoringCredentialKind.SqlServer);
        Assert.Contains(PostgresHealthSensorExecutor.Definition.Parameters, p => p.CredentialKind == MonitoringCredentialKind.PostgreSql);
        Assert.Contains(MySqlHealthSensorExecutor.Definition.Parameters, p => p.CredentialKind == MonitoringCredentialKind.MySql);
        Assert.Contains(ExchangeHealthSensorExecutor.Definition.Parameters, p => p.Key == "winrm.username");
        Assert.Contains(AzureDevOpsServerHealthSensorExecutor.Definition.Parameters, p => p.Key == "devops.url");
        Assert.Equal(TimeSpan.FromMinutes(15), SensorScheduleDefaults.Resolve("ad-health"));
    }

    [Fact]
    public async Task The_devops_address_must_be_http_and_is_quoted_into_the_script()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["devops.url"] = "file:///c:/x";
        var refused = await new AzureDevOpsServerHealthSensorExecutor().ExecuteAsync(new SensorExecutionContext("azure-devops-health", "localhost", settings));
        Assert.Equal(SensorState.Critical, refused.State);
        Assert.Contains("http", refused.Message);
        Assert.Contains("$url = '__MATMON_DEVOPS_URL__'", AzureDevOpsServerHealthSensorExecutor.Script);
    }

    [Theory]
    [InlineData("Unknown table 'GLOBAL_STATUS' in information_schema", true)]
    [InlineData("Access denied for user 'x'", false)]
    [InlineData(null, false)]
    public void Mysql_falls_back_only_when_the_status_table_is_missing(string? message, bool fallback) =>
        Assert.Equal(fallback, MySqlHealthSensorExecutor.IsMissingStatusTable(message));

    [Theory]
    [InlineData("MATMON_DB_HEALTH_MSSQL", "mssql-health")]
    [InlineData("MATMON_DB_HEALTH_POSTGRES", "postgres-health")]
    [InlineData("MATMON_DB_HEALTH_MYSQL", "mysql-health")]
    [InlineData("MATMON_DB_HEALTH_MARIADB", "mysql-health")]
    public async Task A_database_sensor_runs_on_a_real_server(string variable, string type)
    {
        var target = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var parts = target.Split('|');
        var hostPort = parts[0].Split(':');
        var prefix = type switch { "mssql-health" => "mssql", "postgres-health" => "postgres", _ => "mysql" };
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(30) };
        settings.Parameters[$"{prefix}.port"] = hostPort[1];
        settings.Parameters[$"{prefix}.username"] = parts[1];
        settings.Parameters[$"{prefix}.password"] = parts[2];
        if (prefix == "mssql") settings.Parameters["mssql.encrypt"] = "false";
        if (prefix == "postgres") settings.Parameters["postgres.sslMode"] = "Disable";
        SensorThresholdDefaults.Apply(type, settings);

        ISensorExecutor executor = type switch
        {
            "mssql-health" => new MssqlHealthSensorExecutor(),
            "postgres-health" => new PostgresHealthSensorExecutor(),
            _ => new MySqlHealthSensorExecutor()
        };
        var result = await executor.ExecuteAsync(new SensorExecutionContext(type, hostPort[0], settings));
        var channels = result.Channels.Where(channel => !channel.IsVirtual).ToList();
        output.WriteLine($"{variable}: {result.State} - {result.Message} [{string.Join(", ", channels.Select(c => $"{c.Key}={c.Value}"))}]");
        Assert.NotEmpty(channels);
        AssertThresholdsNameChannels(settings, channels);
    }

    /// <summary>
    /// On this machine - not an Exchange server, a domain controller or an Azure DevOps Server - the readings mean nothing, but the
    /// scripts must parse in Windows PowerShell 5.1, return an object the engine reads, and report every channel a default threshold
    /// names (except the ones that only exist where the product is).
    /// </summary>
    [Fact]
    public async Task The_windows_role_sensors_run_through_the_powershell_engine()
    {
        if (Environment.GetEnvironmentVariable("MATMON_ROLE_HEALTH_LOCAL") != "1" || !OperatingSystem.IsWindows())
        {
            return;
        }

        var optional = new[] { "certificateDaysLeft", "timeOffsetSeconds", "appPoolsStopped", "maxTemperature", "securityUpdates" };
        // The message says what the script found in words - on a PC that is no Exchange server / DC / DevOps server, exactly that.
        foreach (var (type, executor, says) in new (string, ISensorExecutor, string)[]
                 {
                     ("exchange-health", new ExchangeHealthSensorExecutor(), "Exchange Management Shell not found"),
                     ("ad-health", new ActiveDirectoryHealthSensorExecutor(), "is this a domain controller?"),
                     ("azure-devops-health", new AzureDevOpsServerHealthSensorExecutor(), "no TFS Job Agent"),
                     ("windows-health", new WindowsHealthSensorExecutor(), "CPU "),
                     ("windows-disk", new WindowsDiskSensorExecutor(), "disk"),
                     ("windows-update", new WindowsUpdateSensorExecutor(), "up to date|pending|search failed")
                 })
        {
            var settings = new MonitoringSettings { Timeout = TimeSpan.FromMinutes(4) };
            SensorThresholdDefaults.Apply(type, settings);
            var result = await executor.ExecuteAsync(new SensorExecutionContext(type, "localhost", settings));
            var channels = result.Channels.Where(channel => !channel.IsVirtual).ToList();
            output.WriteLine($"{type}: {result.State} - {result.Message} [{string.Join(", ", channels.Select(c => $"{c.Key}={c.Value}"))}]");
            Assert.NotEmpty(channels);
            Assert.Matches(says, result.Message);
            Assert.DoesNotContain("message", channels.Select(c => c.Key));
            AssertThresholdsNameChannels(settings, channels, optional);
        }
    }

    private static void AssertThresholdsNameChannels(MonitoringSettings settings, IReadOnlyList<SensorChannelValue> channels, string[]? optional = null)
    {
        foreach (var key in settings.Thresholds.Keys)
        {
            var channel = key["channel:".Length..key.LastIndexOf(':')];
            if (optional?.Contains(channel, StringComparer.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            Assert.True(channels.Any(c => string.Equals(c.Key, channel, StringComparison.OrdinalIgnoreCase)),
                $"default threshold on '{channel}', which the sensor does not report ({string.Join(", ", channels.Select(c => c.Key))})");
        }
    }
}
