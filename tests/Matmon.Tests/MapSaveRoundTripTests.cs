using System.Reflection;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Saving a map must not LOSE tile fields. The store used to normalize a tile by hand-constructing a new
/// MonitoringMapTile from a fixed field list, so every property added to the tile afterwards was silently
/// dropped on save - the widget options and the image/pin fields all went that way at once, and the symptom
/// was the maddening kind: the editor showed the value, the save "succeeded", and the tile came back with
/// defaults.
/// <para>
/// The reflection test below is the real guard: it fails the day someone adds a tile property and the
/// normalizer forgets it, without anyone having to remember this file exists.
/// </para>
/// </summary>
public sealed class MapSaveRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-maproundtrip-" + Guid.NewGuid().ToString("N"));

    public MapSaveRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup must not fail the run.
        }
    }

    private InMemoryMonitoringWorkspaceStore NewStore(ITelemetryRepository telemetry) =>
        new(new RoundTripHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    /// <summary>A tile with EVERY property set to something that is not its default.</summary>
    private static MonitoringMapTile FullyPopulatedTile() => new()
    {
        Id = Guid.NewGuid(),
        Kind = MonitoringMapTileKind.Image,
        Title = "Ground floor",
        ElementId = Guid.NewGuid(),
        TargetTag = null,                     // mutually exclusive with ElementId
        Text = "Server room",
        TargetTokens = ["tag:core", Guid.NewGuid().ToString()],
        IconKey = "probe",
        ShowCard = false,
        Column = 2,
        Row = 2,
        ColumnSpan = 4,
        RowSpan = 4,
        BackgroundColor = "#101827",
        AccentColor = "#78d5c8",
        TextColor = "#ffffff",
        GraphType = MonitoringMapTileGraphType.Bars,
        VisualType = MonitoringMapTileVisualType.Gauge,
        ShowTitle = false,
        ShowStateBadge = false,
        ShowElementName = false,
        ListMode = MonitoringMapListMode.TopValue,
        ListLimit = 9,
        ListChannelKey = "cpu",
        SlaWindowDays = 30,
        ImageAssetId = Guid.NewGuid(),
        ImageFit = MonitoringMapImageFit.Cover,
        ColorRules = new Dictionary<string, string> { ["up"] = "#11aa22", ["down"] = "#cc2233" },
        RefreshSeconds = 120,
        LinkMapId = Guid.NewGuid(),
        EmbedUrl = "https://camera.example/snapshot.jpg",
        EmbedImage = true,
        RackUnits = 24,
        RackDevices = [new MonitoringMapRackDevice { Name = "Core switch", Position = 20, Height = 1, TargetToken = "tag:core", Icon = "switch" }],
        Pins =
        [
            new MonitoringMapPin
            {
                Label = "Rack 3",
                ShowLabel = false,
                Style = MonitoringMapPinStyle.Tile,
                TargetToken = "tag:core",
                X = 0.25,
                Y = 0.75,
                Latitude = 52.52,
                Longitude = 13.405,
                Icon = "switch",
                LinkMapId = Guid.NewGuid()
            }
        ]
    };

    private MonitoringMap SaveAndReload(MonitoringMapTile tile, string? timeZoneId = null)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);

        var created = store.CreateMap(new MonitoringMap
        {
            Name = "Round trip",
            Columns = 12,
            Rows = 8,
            DisplayTimeZoneId = timeZoneId,
            Slides = [new MonitoringMapSlide { Name = "S1", Tiles = [tile] }]
        });

        return store.FindMap(created.Id)!;
    }

    [Fact]
    public void Every_tile_property_survives_a_save()
    {
        var original = FullyPopulatedTile();

        var saved = SaveAndReload(original).Slides[0].Tiles.Single();

        // Reflection, not a hand-written assertion list - a hand-written list is the same failure mode as the
        // hand-written normalizer that caused this.
        var skipped = new HashSet<string> { nameof(MonitoringMapTile.Pins), nameof(MonitoringMapTile.Links), nameof(MonitoringMapTile.RackDevices) };
        var mismatches = new List<string>();
        foreach (var property in typeof(MonitoringMapTile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (skipped.Contains(property.Name) || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var expected = property.GetValue(original);
            var actual = property.GetValue(saved);

            // Dictionaries compare by REFERENCE, which would make this test fail for a field that round-trips
            // perfectly AND pass for nothing - so collections are compared by content.
            var equal = (expected, actual) switch
            {
                (IDictionary<string, string> left, IDictionary<string, string> right) =>
                    left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value),
                // Same reasoning for lists: a List<string> compares by reference, so a field that round-trips
                // perfectly would fail here and nothing would ever pass.
                (IReadOnlyList<string> left, IReadOnlyList<string> right) => left.SequenceEqual(right),
                _ => Equals(expected, actual)
            };

            if (!equal)
            {
                mismatches.Add($"{property.Name}: expected {expected ?? "null"}, got {actual ?? "null"}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Pins_survive_a_save_with_all_their_fields()
    {
        var original = FullyPopulatedTile();

        var pin = SaveAndReload(original).Slides[0].Tiles.Single().Pins.Single();
        var expected = original.Pins[0];

        Assert.Equal(expected.Label, pin.Label);
        Assert.Equal(expected.ShowLabel, pin.ShowLabel);
        Assert.Equal(expected.Style, pin.Style);
        Assert.Equal(expected.TargetToken, pin.TargetToken);
        Assert.Equal(expected.X, pin.X);
        Assert.Equal(expected.Y, pin.Y);
        Assert.Equal(expected.Latitude, pin.Latitude);
        Assert.Equal(expected.Longitude, pin.Longitude);
        Assert.Equal(expected.Icon, pin.Icon);
        Assert.Equal(expected.LinkMapId, pin.LinkMapId);
    }

    [Fact]
    public void Rack_devices_survive_a_save()
    {
        var original = FullyPopulatedTile();

        var device = Assert.Single(SaveAndReload(original).Slides[0].Tiles.Single().RackDevices);

        var expected = original.RackDevices[0];
        Assert.Equal(expected.Id, device.Id);
        Assert.Equal(expected.Name, device.Name);
        Assert.Equal(expected.Position, device.Position);
        Assert.Equal(expected.Height, device.Height);
        Assert.Equal(expected.TargetToken, device.TargetToken);
        Assert.Equal(expected.Icon, device.Icon);
    }

    [Fact]
    public void Topology_links_survive_a_save()
    {
        var tile = FullyPopulatedTile();
        tile.Kind = MonitoringMapTileKind.Topology;
        var second = new MonitoringMapPin { Label = "Core", Style = MonitoringMapPinStyle.Device, Icon = "router", X = 0.5, Y = 0.2 };
        tile.Pins.Add(second);
        var link = new MonitoringMapLink
        {
            FromPinId = tile.Pins[0].Id,
            ToPinId = second.Id,
            TargetToken = "tag:uplink",
            Label = "Gi0/1",
            ShowTraffic = false
        };
        tile.Links.Add(link);

        var saved = SaveAndReload(tile).Slides[0].Tiles.Single();

        var stored = Assert.Single(saved.Links);
        Assert.Equal(link.Id, stored.Id);
        Assert.Equal(link.FromPinId, stored.FromPinId);
        Assert.Equal(link.ToPinId, stored.ToPinId);
        Assert.Equal(link.TargetToken, stored.TargetToken);
        Assert.Equal(link.Label, stored.Label);
        Assert.False(stored.ShowTraffic);
    }

    [Fact]
    public void The_map_timezone_survives_a_save()
    {
        var saved = SaveAndReload(FullyPopulatedTile(), "Europe/Berlin");

        Assert.Equal("Europe/Berlin", saved.DisplayTimeZoneId);
    }

    [Fact]
    public void A_pin_with_an_impossible_coordinate_is_dropped_rather_than_stored()
    {
        var tile = FullyPopulatedTile();
        tile.Pins[0].Latitude = 500;
        tile.Pins[0].Longitude = 190;

        var pin = SaveAndReload(tile).Slides[0].Tiles.Single().Pins.Single();

        Assert.Null(pin.Latitude);
        // 190 is a real longitude, just unnormalised - it wraps rather than being thrown away.
        Assert.Equal(-170, pin.Longitude);
    }

    [Fact]
    public void A_picture_only_tile_is_not_discarded_as_empty()
    {
        // The "is this tile empty?" check used to look at title/text/target only, so a floorplan whose title
        // had been cleared vanished on save along with its picture and pins.
        var tile = FullyPopulatedTile();
        tile.Title = string.Empty;
        tile.Text = null;
        tile.ElementId = null;

        Assert.Single(SaveAndReload(tile).Slides[0].Tiles);
    }
}

file sealed class RoundTripHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
