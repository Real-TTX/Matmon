using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>
/// What counts as a cloud link carried by the ENVIRONMENT. It matters because a workspace that has not taken over in
/// the UI links itself to it: a fresh installation started with the same environment IS that cloud instance again,
/// license included. The pages show that, so they must agree with the connection service on what a link is - and a
/// bare <c>Matmon__CloudUrl</c> (which only prefills the connect form) must not read as "connected".
/// </summary>
public class CloudBootstrapLinkTests
{
    private static readonly string AnInstanceId = Guid.NewGuid().ToString();

    private static MatmonRuntimeOptions Options(string? url, string? id, string? token) =>
        new() { CloudUrl = url, CloudInstanceId = id, CloudInstanceToken = token };

    [Fact]
    public void AUrlAnInstanceIdAndATokenAreALink() =>
        Assert.True(Options("http://matmon-cloud:8055", AnInstanceId, "secret").HasCloudBootstrapLink);

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("http://matmon-cloud:8055", null, null)]            // a bare URL only prefills the form
    [InlineData("http://matmon-cloud:8055", "not-a-guid", "secret")] // the connection service refuses it too
    [InlineData("http://matmon-cloud:8055", "", "secret")]
    [InlineData("   ", "00000000-0000-0000-0000-000000000001", "secret")]
    public void AnIncompleteEnvironmentIsNotALink(string? url, string? id, string? token) =>
        Assert.False(Options(url, id, token).HasCloudBootstrapLink);

    [Fact]
    public void ALinkWithoutATokenIsNotALink() =>
        Assert.False(Options("http://matmon-cloud:8055", AnInstanceId, "  ").HasCloudBootstrapLink);
}
