using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class MapRackTests
{
    [Fact]
    public void A_device_is_kept_inside_the_rack_and_the_unit_count_in_range()
    {
        var (units, devices) = MonitoringMapRack.Normalize(100,
        [
            new MonitoringMapRackDevice { Name = " Server ", Position = 60, Height = 2 },
            new MonitoringMapRackDevice { Name = "Shelf", Position = 0, Height = 0 }
        ]);

        Assert.Equal(MonitoringMapRack.MaxUnits, units);
        var server = devices.Single(device => device.Name == "Server");
        Assert.Equal(MonitoringMapRack.MaxUnits - 1, server.Position);
        Assert.Equal(MonitoringMapRack.MaxUnits, server.Top);
        var shelf = devices.Single(device => device.Name == "Shelf");
        Assert.Equal(1, shelf.Position);
        Assert.Equal(1, shelf.Height);
        // Top of the rack first - the order it is drawn in.
        Assert.Equal("Server", devices[0].Name);
    }

    [Fact]
    public void Devices_that_share_a_unit_are_kept_and_named_as_colliding()
    {
        var a = new MonitoringMapRackDevice { Name = "A", Position = 10, Height = 2 };
        var b = new MonitoringMapRackDevice { Name = "B", Position = 11, Height = 1 };
        var c = new MonitoringMapRackDevice { Name = "C", Position = 12, Height = 1 };

        var (_, devices) = MonitoringMapRack.Normalize(42, [a, b, c]);
        var colliding = MonitoringMapRack.Collisions(devices);

        Assert.Equal(3, devices.Count);
        Assert.Contains(a.Id, colliding);
        Assert.Contains(b.Id, colliding);
        Assert.DoesNotContain(c.Id, colliding);
    }
}
