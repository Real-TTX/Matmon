using Matmon.Core.Domain;

namespace Matmon.Tests;

// Which alerts a rule mails about - including the tag target, which cascades like every other tag.
public sealed class NotificationRuleTargetingTests
{
    private static readonly ProbeElement Root = new("Main") { Id = Guid.NewGuid() };
    private static readonly FolderElement Site = new("Berlin") { Id = Guid.NewGuid(), ParentId = Root.Id, Tags = ["production"] };
    private static readonly HostElement Server = new("srv-01") { Id = Guid.NewGuid(), ParentId = Site.Id };
    private static readonly SensorElement Cpu = new("CPU", "local-health", string.Empty) { Id = Guid.NewGuid(), ParentId = Server.Id };
    private static readonly SensorElement Lab = new("Lab ping", "ping", "lab") { Id = Guid.NewGuid(), ParentId = Root.Id, Tags = ["lab"] };

    private static readonly IReadOnlyDictionary<Guid, MonitoringElement> Elements =
        new MonitoringElement[] { Root, Site, Server, Cpu, Lab }.ToDictionary(element => element.Id);

    [Fact]
    public void NoTargetCoversEverything()
    {
        Assert.True(NotificationRuleTargeting.Matches(new NotificationRule(), Lab.Id, Elements));
    }

    [Fact]
    public void ATagTargetCoversEverySensorThatInheritsTheTag()
    {
        var rule = new NotificationRule { TargetTag = "Production" };

        Assert.True(NotificationRuleTargeting.Matches(rule, Cpu.Id, Elements));   // tagged two levels up
        Assert.False(NotificationRuleTargeting.Matches(rule, Lab.Id, Elements));  // different branch, different tag
    }

    [Fact]
    public void AnElementTargetStillCoversItsSubtreeOrOnlyItself()
    {
        Assert.True(NotificationRuleTargeting.Matches(new NotificationRule { TargetElementId = Site.Id }, Cpu.Id, Elements));
        Assert.False(NotificationRuleTargeting.Matches(new NotificationRule { TargetElementId = Site.Id, IncludeDescendants = false }, Cpu.Id, Elements));
    }

    [Fact]
    public void AnUnknownElementIsNotCoveredByATag()
    {
        Assert.False(NotificationRuleTargeting.Matches(new NotificationRule { TargetTag = "production" }, Guid.NewGuid(), Elements));
    }
}
