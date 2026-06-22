using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class MonitoringTargetResolverTests
{
    [Fact]
    public void Tag_token_is_recognized_and_name_extracted()
    {
        Assert.True(MonitoringTargetResolver.IsTag("tag:berlin"));
        Assert.Equal("berlin", MonitoringTargetResolver.TagName("tag:berlin"));
        Assert.Equal("Berlin", MonitoringTargetResolver.TagName("tag: Berlin ")); // trimmed, spelling kept
        Assert.Null(MonitoringTargetResolver.ElementId("tag:berlin"));
    }

    [Fact]
    public void Guid_token_is_an_element_target_not_a_tag()
    {
        var id = Guid.NewGuid();
        var token = MonitoringTargetResolver.ForElement(id);

        Assert.False(MonitoringTargetResolver.IsTag(token));
        Assert.Null(MonitoringTargetResolver.TagName(token));
        Assert.Equal(id, MonitoringTargetResolver.ElementId(token));
    }

    [Fact]
    public void Empty_or_garbage_token_resolves_to_nothing()
    {
        foreach (var token in new[] { null, "", "   ", "not-a-guid", "tag:" })
        {
            Assert.Null(MonitoringTargetResolver.ElementId(token));
            Assert.Null(MonitoringTargetResolver.TagName(token));
        }
    }

    [Fact]
    public void ForTag_builds_a_trimmed_tag_token()
    {
        Assert.Equal("tag:Berlin", MonitoringTargetResolver.ForTag(" Berlin "));
    }
}
