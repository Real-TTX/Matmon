using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Tests;

// Probe Health moved into Matmon.Probe so the agent can run it too. These pin what it judges now that the
// storage behind it is an abstraction: the agent's own directory rather than the Host's workspace.
public sealed class ProbeHealthSensorTests
{
    private sealed class FixedStorage(ProbeStorageSnapshot snapshot) : IProbeStorageSource
    {
        public ProbeStorageSnapshot GetSnapshot() => snapshot;
    }

    private static readonly ProbeStorageSnapshot Roomy = new(true, 5 * 1024 * 1024, 12, 50L * 1024 * 1024 * 1024, 60, null);

    private static async Task<SensorExecutionResult> RunAsync(
        AppMode mode, ProbeStorageSnapshot storage, bool connected, MonitoringSettings? settings = null)
    {
        var state = new SlaveProbeRuntimeState();
        state.RecordHeartbeat(connected, connected ? "ok" : "primary unreachable");
        var executor = new ProbeHealthSensorExecutor(new ProbeRuntimeOptions { Mode = mode }, state, new FixedStorage(storage));
        return await executor.ExecuteAsync(new SensorExecutionContext("probe-health", string.Empty, settings ?? new MonitoringSettings()));
    }

    [Fact]
    public async Task ConnectedSecondaryWithRoomIsHealthy()
    {
        var result = await RunAsync(AppMode.Secondary, Roomy, connected: true);

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("storageFreePercent", result.DefaultChannelKey);
        Assert.Equal(60, result.Value);
    }

    [Fact]
    public async Task DisconnectedSecondaryIsCriticalByDefault()
    {
        var result = await RunAsync(AppMode.Secondary, Roomy, connected: false);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("primary unreachable", result.Message);
    }

    [Fact]
    public async Task APrimaryIsNeverDisconnectedFromItself()
    {
        var result = await RunAsync(AppMode.Primary, Roomy, connected: false);

        Assert.Equal(SensorState.Healthy, result.State);
    }

    [Theory]
    [InlineData(14, SensorState.Warning)]
    [InlineData(8, SensorState.Critical)]
    public async Task LowFreeSpaceEscalates(double freePercent, SensorState expected)
    {
        var result = await RunAsync(AppMode.Secondary, Roomy with { DriveFreePercent = freePercent }, connected: true);

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public async Task WithoutADriveTheDataSizeIsTheReading()
    {
        var result = await RunAsync(
            AppMode.Secondary, Roomy with { DriveFreePercent = null, DriveAvailableBytes = null }, connected: true);

        Assert.Equal("dataUsedMb", result.DefaultChannelKey);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void DirectorySourceMeasuresTheDirectoryAndItsDrive()
    {
        var directory = Directory.CreateTempSubdirectory("matmon-probe-storage-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "a.bin"), new byte[1000]);
            Directory.CreateDirectory(Path.Combine(directory.FullName, "sub"));
            File.WriteAllBytes(Path.Combine(directory.FullName, "sub", "b.bin"), new byte[500]);

            var snapshot = new DirectoryProbeStorageSource(directory.FullName).GetSnapshot();

            Assert.True(snapshot.DataDirectoryExists);
            Assert.Equal(1500, snapshot.DataDirectoryBytes);
            Assert.Equal(2, snapshot.DataFileCount);
            Assert.NotNull(snapshot.DriveFreePercent);
            Assert.Null(snapshot.ErrorMessage);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AMissingDirectoryIsReportedNotThrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), "matmon-missing-" + Guid.NewGuid().ToString("N"));

        var snapshot = new DirectoryProbeStorageSource(missing).GetSnapshot();

        Assert.False(snapshot.DataDirectoryExists);
        Assert.Equal(0, snapshot.DataFileCount);
        Assert.NotNull(snapshot.ErrorMessage);
    }
}
