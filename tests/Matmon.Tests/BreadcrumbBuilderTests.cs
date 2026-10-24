using Matmon.Host.Ui;

namespace Matmon.Tests;

/// <summary>
/// A breadcrumb trail starts at the MENU ITEM a page belongs to - never at "Monitoring" (no menu item has that name) and
/// never under a section the page is not in (Probes used to sit under Sensors, new hosts under an "Infrastructure" page
/// the menu does not have).
/// </summary>
public sealed class BreadcrumbBuilderTests
{
    private static string Trail(string path, string? title = null) =>
        string.Join(" > ", BreadcrumbBuilder.Build(path, title).Select(item => item.Page is null ? item.Label : $"{item.Label}({item.Page})"));

    [Theory]
    [InlineData("/Probes", "Probes")]
    [InlineData("/agents", "Probes(/Probes) > Agents")]
    [InlineData("/system/probes/new", "Probes(/Probes) > New probe")]
    [InlineData("/system/probes/install", "Probes(/Probes) > Install")]
    [InlineData("/monitoring/probe/0f3c2b6e-9a51-4f5b-8f0e-6c2f7d0b1a11/usage", "Probes(/Probes) > Usage")]
    [InlineData("/monitoring", "Sensors")]
    [InlineData("/monitoring/sensor/new", "Sensors(/Monitoring) > New sensor")]
    [InlineData("/monitoring/sensor/0f3c2b6e-9a51-4f5b-8f0e-6c2f7d0b1a11", "Sensors(/Monitoring) > Usage")]
    [InlineData("/monitoring/host/new", "Sensors(/Monitoring) > New host")]
    [InlineData("/Config", "System")]
    [InlineData("/Logs", "System(/Config) > Logs")]
    [InlineData("/system/users/new", "System(/Config) > Users(/Config?tab=users) > New user")]
    [InlineData("/notifications/rule", "Notifications(/Notifications) > Usage")]
    [InlineData("/Discovery", "Usage")]
    public void A_trail_starts_at_the_menu_item(string path, string expected) =>
        Assert.Equal(expected, Trail(path, "Usage"));

    [Theory]
    [InlineData("/")]
    [InlineData("/Index")]
    [InlineData("/login")]
    public void The_home_page_has_no_trail(string path) =>
        Assert.Empty(BreadcrumbBuilder.Build(path, "Dashboard"));

    [Fact]
    public void No_trail_mentions_monitoring_any_more()
    {
        string[] paths = ["/Probes", "/agents", "/monitoring", "/monitoring/element", "/maps/edit", "/Config", "/Logs", "/paused", "/Events", "/something/else"];
        Assert.All(paths, path => Assert.DoesNotContain(BreadcrumbBuilder.Build(path, "Page"), item => item.Label == "Monitoring"));
    }

    [Fact]
    public void A_page_can_pass_its_own_trail()
    {
        var own = new[] { new BreadcrumbItem("System", "/Config"), new BreadcrumbItem("Plugins") };
        Assert.Same(own[1], BreadcrumbBuilder.Build("/system/plugins", "System", own)[1]);
    }
}
