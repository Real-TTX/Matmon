using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>The Synology Health sensor used to report free space from the RAID table - i.e. the storage POOL,
/// not the volume. Volumes (/volume1, /volume2, …) live in the HOST-RESOURCES-MIB hrStorageTable. These pin the
/// volume parsing so the default reading is Volume 1 and every volume is picked up (N volumes / N pools).</summary>
public class SynologyVolumeParsingTests
{
    private const string HrStorageRoot = "1.3.6.1.2.1.25.2.3.1";

    private static SnmpDiscoveryItem Cell(int column, int row, string value, bool numeric) =>
        new($"{HrStorageRoot}.{column}.{row}", numeric ? "INTEGER" : "STRING", value, numeric, false);

    // hrStorage columns: 3 = descr, 4 = allocation units (bytes), 5 = size (units), 6 = used (units).
    private static IEnumerable<SnmpDiscoveryItem> Row(int row, string descr, string? unit, string? size, string? used)
    {
        yield return Cell(3, row, descr, false);
        if (unit is not null) { yield return Cell(4, row, unit, true); }
        if (size is not null) { yield return Cell(5, row, size, true); }
        if (used is not null) { yield return Cell(6, row, used, true); }
    }

    [Fact]
    public void Parses_each_numbered_volume_with_byte_totals()
    {
        var items = new List<SnmpDiscoveryItem>();
        items.AddRange(Row(31, "/volume1", "4096", "1000000", "250000"));
        items.AddRange(Row(32, "/volume2", "4096", "2000000", "100000"));

        var volumes = SynologyHealthSensorExecutor.ParseVolumeSnapshots(items);

        Assert.Equal(2, volumes.Count);

        var v1 = volumes.Single(v => v.Index == 1);
        Assert.Equal("Volume 1", v1.DisplayName);
        Assert.Equal(4096d * 1000000d, v1.TotalBytes);
        Assert.Equal(4096d * 750000d, v1.FreeBytes);

        var v2 = volumes.Single(v => v.Index == 2);
        Assert.Equal(4096d * 2000000d, v2.TotalBytes);
        Assert.Equal(4096d * 1900000d, v2.FreeBytes);
    }

    [Fact]
    public void Ignores_non_volume_filesystems()
    {
        var items = new List<SnmpDiscoveryItem>();
        items.AddRange(Row(1, "/", "4096", "500000", "300000"));            // DSM system partition
        items.AddRange(Row(2, "/tmp", "4096", "10000", "1000"));            // tmpfs
        items.AddRange(Row(40, "/volumeUSB1", "4096", "80000", "1000"));    // external USB - not a numbered volume
        items.AddRange(Row(31, "/volume1", "4096", "1000000", "250000"));

        var volumes = SynologyHealthSensorExecutor.ParseVolumeSnapshots(items);

        Assert.Single(volumes);
        Assert.Equal(1, volumes[0].Index);
    }

    [Fact]
    public void Volume_with_no_used_column_still_reports_total_but_no_free()
    {
        var items = Row(33, "/volume3", "4096", "1000000", null).ToList();

        var volume = Assert.Single(SynologyHealthSensorExecutor.ParseVolumeSnapshots(items));

        Assert.Equal(4096d * 1000000d, volume.TotalBytes);
        Assert.Null(volume.FreeBytes);
    }

    [Fact]
    public void Rows_missing_size_or_allocation_unit_are_skipped()
    {
        var items = new List<SnmpDiscoveryItem>();
        items.AddRange(Row(31, "/volume1", unit: null, size: "1000000", used: "1"));  // no allocation unit
        items.AddRange(Row(32, "/volume2", "4096", size: null, used: "1"));           // no size

        Assert.Empty(SynologyHealthSensorExecutor.ParseVolumeSnapshots(items));
    }

    [Fact]
    public void Primary_volume_is_volume_one_regardless_of_walk_order()
    {
        var items = new List<SnmpDiscoveryItem>();
        items.AddRange(Row(32, "/volume2", "4096", "2000000", "0"));
        items.AddRange(Row(31, "/volume1", "4096", "1000000", "0"));

        var primary = SynologyHealthSensorExecutor.SelectPrimaryVolume(
            SynologyHealthSensorExecutor.ParseVolumeSnapshots(items));

        Assert.NotNull(primary);
        Assert.Equal(1, primary!.Index);
    }

    [Fact]
    public void No_volumes_reported_yields_no_primary()
    {
        Assert.Null(SynologyHealthSensorExecutor.SelectPrimaryVolume([]));
    }
}
