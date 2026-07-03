using Matmon.Core.Domain;

namespace Matmon.Tests;

public class MonitoringElementCloneTests
{
    [Fact]
    public void SensorClone_preserves_identity_and_fields()
    {
        var sensor = new SensorElement("Ping", "ping", "1.2.3.4")
        {
            Id = Guid.NewGuid(),
            IsPaused = true,
            Description = "desc",
            Tags = ["a", "b"],
            ParentId = Guid.NewGuid(),
            TemplateOriginId = Guid.NewGuid()
        };
        sensor.Settings.Parameters["k"] = "v";

        var clone = (SensorElement)sensor.Clone();

        Assert.Equal(sensor.Id, clone.Id);
        Assert.Equal("Ping", clone.Name);
        Assert.Equal("ping", clone.SensorTypeKey);
        Assert.Equal("1.2.3.4", clone.Target);
        Assert.True(clone.IsPaused);
        Assert.Equal("desc", clone.Description);
        Assert.Equal(sensor.ParentId, clone.ParentId);
        Assert.Equal(sensor.TemplateOriginId, clone.TemplateOriginId);
        Assert.Equal(["a", "b"], clone.Tags);
        Assert.Equal("v", clone.Settings.Parameters["k"]);
    }

    [Fact]
    public void SensorClone_is_detached_mutating_clone_does_not_touch_original()
    {
        var sensor = new SensorElement("Ping", "ping", "1.2.3.4");
        sensor.Tags.Add("keep");
        sensor.Settings.Parameters["k"] = "original";

        var clone = (SensorElement)sensor.Clone();
        clone.Name = "changed";
        clone.Target = "9.9.9.9";
        clone.IsPaused = true;
        clone.Tags.Add("extra");
        clone.Tags.Clear();
        clone.Settings.Parameters["k"] = "mutated";
        clone.Settings.Parameters["new"] = "x";

        Assert.Equal("Ping", sensor.Name);
        Assert.Equal("1.2.3.4", sensor.Target);
        Assert.False(sensor.IsPaused);
        Assert.Equal(["keep"], sensor.Tags);
        Assert.Equal("original", sensor.Settings.Parameters["k"]);
        Assert.False(sensor.Settings.Parameters.ContainsKey("new"));
    }

    [Fact]
    public void ContainerClone_deep_copies_children()
    {
        var probe = new ProbeElement("probe")
        {
            Id = Guid.NewGuid(),
            ProbeId = "p1",
            EnrollmentToken = "token",
            Subnets = ["10.0.0.0/24"]
        };
        var folder = new FolderElement("folder") { Id = Guid.NewGuid() };
        var host = new HostElement("host") { Id = Guid.NewGuid(), Address = "10.0.0.5" };
        var sensor = new SensorElement("Ping", "ping", "10.0.0.5") { Id = Guid.NewGuid() };
        host.Children.Add(sensor);
        folder.Children.Add(host);
        probe.Children.Add(folder);

        var clone = (ProbeElement)probe.Clone();

        Assert.Equal("p1", clone.ProbeId);
        Assert.Equal("token", clone.EnrollmentToken);
        Assert.Equal(["10.0.0.0/24"], clone.Subnets);

        // Structure preserved with matching ids.
        var clonedFolder = Assert.IsType<FolderElement>(Assert.Single(clone.Children));
        var clonedHost = Assert.IsType<HostElement>(Assert.Single(clonedFolder.Children));
        var clonedSensor = Assert.IsType<SensorElement>(Assert.Single(clonedHost.Children));
        Assert.Equal(sensor.Id, clonedSensor.Id);
        Assert.Equal("10.0.0.5", clonedHost.Address);

        // Child objects are distinct instances.
        Assert.NotSame(sensor, clonedSensor);
        Assert.NotSame(host, clonedHost);

        // Mutating the clone's subtree and subnets does not affect the original.
        clonedSensor.Name = "changed";
        clonedHost.Address = "0.0.0.0";
        clone.Children.Clear();
        clone.Subnets.Add("192.168.0.0/24");

        Assert.Equal("Ping", sensor.Name);
        Assert.Equal("10.0.0.5", host.Address);
        Assert.Single(probe.Children);
        Assert.Equal(["10.0.0.0/24"], probe.Subnets);
    }

    [Fact]
    public void TemplateClone_is_detached()
    {
        var template = new MonitoringTemplate
        {
            Id = Guid.NewGuid(),
            Key = "k",
            Name = "T",
            TargetKind = MonitoringTemplateScope.Sensor,
            SensorTypeKey = "ping",
            ParentTemplateId = Guid.NewGuid(),
            Tags = ["t1"]
        };
        template.Settings.Parameters["p"] = "1";

        var clone = template.Clone();
        clone.Name = "changed";
        clone.Tags.Add("t2");
        clone.Settings.Parameters["p"] = "2";

        Assert.Equal(template.Id, clone.Id);
        Assert.Equal("ping", clone.SensorTypeKey);
        Assert.Equal(template.ParentTemplateId, clone.ParentTemplateId);
        Assert.Equal("T", template.Name);
        Assert.Equal(["t1"], template.Tags);
        Assert.Equal("1", template.Settings.Parameters["p"]);
    }
}
