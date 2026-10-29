using System.Net;
using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>The HTTP Advanced sensor's content checks - what a cloud HTTP sensor runs too: text that must be there, text that
/// must not, a pattern that must match, a status class, and HEAD (no body, so no content checks).</summary>
public sealed class HttpAdvancedContentCheckTests
{
    private const string Page = "<html><body><h1>Example Domain</h1><p>Version 4.12 is running</p></body></html>";

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int BodyBytesServed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Method == HttpMethod.Head ? [] : Encoding.UTF8.GetBytes(body);
            BodyBytesServed = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static async Task<SensorExecutionResult> Run(Dictionary<string, string> parameters, HttpStatusCode status = HttpStatusCode.OK, string body = Page, int? maxBody = null)
    {
        var handler = new FixedHandler(status, body);
        var executor = maxBody is int max ? new HttpAdvancedSensorExecutor(new HttpClient(handler)) { MaxBodyBytes = max } : new HttpAdvancedSensorExecutor(new HttpClient(handler));
        var settings = new MonitoringSettings { Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase) };
        return await executor.ExecuteAsync(new SensorExecutionContext("http-advanced", "https://example.com/", settings));
    }

    [Fact]
    public async Task Text_that_is_there_passes_and_text_that_is_missing_fails()
    {
        Assert.Equal(SensorState.Healthy, (await Run(new() { ["expectedText"] = "example domain" })).State);

        var missing = await Run(new() { ["expectedText"] = "Welcome" });
        Assert.Equal(SensorState.Critical, missing.State);
        Assert.Equal("expected text missing", missing.Message);
    }

    [Fact]
    public async Task Text_that_must_not_appear_fails_when_it_does()
    {
        Assert.Equal(SensorState.Healthy, (await Run(new() { ["unexpectedText"] = "maintenance" })).State);

        var found = await Run(new() { ["unexpectedText"] = "is running" });
        Assert.Equal(SensorState.Critical, found.State);
        Assert.Contains("unexpected text found", found.Message);
    }

    [Fact]
    public async Task A_pattern_must_match_and_an_invalid_one_says_so()
    {
        Assert.Equal(SensorState.Healthy, (await Run(new() { ["expectedPattern"] = @"Version \d+\.\d+" })).State);

        var noMatch = await Run(new() { ["expectedPattern"] = @"Version 5\.\d+" });
        Assert.Equal(SensorState.Critical, noMatch.State);
        Assert.Equal("pattern did not match", noMatch.Message);

        var invalid = await Run(new() { ["expectedPattern"] = "(unclosed" });
        Assert.Equal(SensorState.Critical, invalid.State);
        Assert.StartsWith("invalid pattern", invalid.Message);
    }

    [Fact]
    public void A_catastrophic_pattern_is_stopped_by_the_time_limit()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["expectedPattern"] = "^(a+)+$";
        var body = new string('a', 40_000) + "!";

        Assert.Equal("pattern took too long to match", HttpAdvancedSensorExecutor.CheckContent(settings, body));
    }

    [Theory]
    [InlineData("2xx", HttpStatusCode.NoContent, true)]
    [InlineData("2xx", HttpStatusCode.NotFound, false)]
    [InlineData("3XX", HttpStatusCode.MovedPermanently, true)]
    [InlineData("404", HttpStatusCode.NotFound, true)]
    [InlineData("", HttpStatusCode.NoContent, false)] // a missing value still means 200, as it always did
    [InlineData("", HttpStatusCode.OK, true)]
    public async Task The_expected_status_is_a_code_or_a_class(string expected, HttpStatusCode status, bool healthy)
    {
        var parameters = expected.Length == 0 ? new Dictionary<string, string>() : new() { ["expectedStatus"] = expected };
        Assert.Equal(healthy ? SensorState.Healthy : SensorState.Critical, (await Run(parameters, status, string.Empty)).State);
    }

    [Fact]
    public async Task Head_skips_the_content_checks()
    {
        var result = await Run(new() { ["method"] = "HEAD", ["expectedText"] = "Example Domain", ["expectedPattern"] = "nothing" });
        Assert.Equal(SensorState.Healthy, result.State);
    }

    [Fact]
    public async Task The_body_is_read_up_to_the_cap_only()
    {
        // the text sits past the first 2 KB: with a 1 KB cap it is not seen
        var body = new string('x', 2048) + "needle";
        Assert.Equal(SensorState.Healthy, (await Run(new() { ["expectedText"] = "needle" }, body: body)).State);
        Assert.Equal(SensorState.Critical, (await Run(new() { ["expectedText"] = "needle" }, body: body, maxBody: 1024)).State);
    }
}
