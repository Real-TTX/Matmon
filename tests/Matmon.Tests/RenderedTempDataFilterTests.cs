using Matmon.Host.Pages;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>
/// The filter that keeps a message shown now from turning up again on the next page clears a page's [TempData]
/// properties - the strings and nullable values, never a plain value type it could not "unset". (The behaviour itself -
/// rendered once, then gone; a redirect message still shown once - was checked against the running app.)
/// </summary>
public sealed class RenderedTempDataFilterTests
{
    [Fact]
    public void It_finds_the_message_properties_of_a_page()
    {
        var names = RenderedTempDataFilter.TempDataProperties(typeof(SystemSnmpTrapsModel)).Select(property => property.Name).ToArray();

        Assert.Contains("StatusMessage", names);
        Assert.Contains("ErrorMessage", names);
    }

    [Fact]
    public void It_leaves_value_types_alone()
    {
        var properties = RenderedTempDataFilter.TempDataProperties(typeof(AgentsModel));

        Assert.Contains(properties, property => property.Name == "IssuedExpiresUtc"); // DateTime? - can be cleared
        Assert.DoesNotContain(properties, property => property.Name == "IssuedReplaces"); // bool - cannot
    }
}
