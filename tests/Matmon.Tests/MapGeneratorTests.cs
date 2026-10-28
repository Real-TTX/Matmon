using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

public sealed class MapGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-mapgen-" + Guid.NewGuid().ToString("N"));

    public MapGeneratorTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void A_folder_becomes_an_overview_and_one_tile_per_machine_without_overlaps()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new GeneratorHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
        var folder = store.CreateFolder(rootId, "Office", null);
        foreach (var name in new[] { "nas", "printer", "switch" })
        {
            var host = store.CreateHost(folder.Id, name, name, null);
            store.CreateSensor(host.Id, "Ping", "ping", name, null);
            store.CreateSensor(host.Id, "HTTP", "http", $"http://{name}", null);
        }

        var map = new MapGenerator(store).Generate("Office", folder.Id.ToString(), overview: true, tilePerMachine: true);

        var tiles = Assert.Single(map.Slides).Tiles;
        Assert.Contains(tiles, tile => tile.Kind == MonitoringMapTileKind.Heading);
        Assert.Contains(tiles, tile => tile.Kind == MonitoringMapTileKind.Honeycomb && tile.ElementId == folder.Id);
        Assert.Equal(3, tiles.Count(tile => tile.Kind == MonitoringMapTileKind.Status));
        var cells = new HashSet<(int, int)>();
        foreach (var tile in tiles)
        {
            for (var column = tile.Column; column < tile.Column + tile.ColumnSpan; column++)
            {
                for (var row = tile.Row; row < tile.Row + tile.RowSpan; row++)
                {
                    Assert.True(cells.Add((column, row)), $"Two tiles share cell {column},{row}.");
                }
            }
        }
    }

    [Fact]
    public void A_target_without_sensors_is_refused_in_words()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new GeneratorHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

        var problem = Assert.Throws<InvalidOperationException>(() => new MapGenerator(store).Generate("x", "tag:nothing-here", true, true));
        Assert.Contains("no sensors", problem.Message);
    }
}

file sealed class GeneratorHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
