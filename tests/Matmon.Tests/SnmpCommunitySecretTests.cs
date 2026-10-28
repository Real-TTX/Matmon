using Matmon.Core.Domain;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Telemetry;
using Matmon.Host.Pages;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The SNMP v1/v2c community is the device's password: on the wire it is plain (the protocol's limit), but on disk it is
/// sealed like every other secret - a sensor's own community, and the communities the trap processing accepts.
/// </summary>
public sealed class SnmpCommunitySecretTests : IDisposable
{
    private const string Community = "s3cret-ro-community";
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-community-").FullName;
    private readonly EphemeralDataProtectionProvider _protection = new();

    private InMemoryMonitoringWorkspaceStore NewStore(SqliteTelemetryRepository telemetry) =>
        new(new CommunityHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            _protection,
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    [Fact]
    public void The_community_parameter_is_declared_a_secret()
    {
        var definition = SnmpSensorExecutor.Definition.Parameters.Single(parameter => parameter.Key == "snmp.community");
        Assert.Equal(SensorParameterKind.Secret, definition.Kind);
    }

    [Fact]
    public void A_sensor_community_and_the_trap_communities_never_reach_the_disk_in_the_clear()
    {
        Guid sensorId;
        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t1.db")))
        using (var store = NewStore(telemetry))
        {
            var root = store.GetAllElements().OfType<ProbeElement>().First().Id;
            sensorId = store.CreateSensor(root, "Switch", "snmp", "10.0.0.2", null).Id;
            store.UpdateElement(sensorId, element => element.Settings.Parameters["snmp.community"] = Community);
            store.UpdateSnmpTrapSettings(new SnmpTrapSettings { Communities = "trap-community-1, trap-community-2" });
        }

        var json = File.ReadAllText(Path.Combine(_dir, "ws.json"));
        Assert.DoesNotContain(Community, json);
        Assert.DoesNotContain("trap-community-1", json);

        using var telemetry2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db"));
        using var reopened = NewStore(telemetry2);
        Assert.Equal(Community, reopened.FindElement(sensorId)!.Settings.Parameters["snmp.community"]);
        Assert.Equal(["trap-community-1", "trap-community-2"], reopened.GetSnmpTrapSettings().CommunityList());
    }

    [Fact]
    public void The_trap_form_keeps_the_stored_communities_when_left_blank_and_clears_them_only_when_asked()
    {
        const string stored = "public, ups";

        Assert.True(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { Communities = "" }, out var kept, out _, stored));
        Assert.Equal(["public", "ups"], kept.CommunityList());

        Assert.True(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { Communities = "other" }, out var replaced, out _, stored));
        Assert.Equal(["other"], replaced.CommunityList());

        Assert.True(SystemSnmpTrapsModel.TryBuildSettings(new SnmpTrapSettingsInput { ClearCommunities = true }, out var cleared, out _, stored));
        Assert.Empty(cleared.CommunityList());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

file sealed class CommunityHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
