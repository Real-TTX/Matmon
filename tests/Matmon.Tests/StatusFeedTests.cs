using System.Net;
using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// A provider's status page as channels: HTTP Advanced walking a JSON list with [*] (one channel per service, its
/// status text read as OK / warning / critical), and the RSS / Atom feed sensor reading open incidents from the updates.
/// The data has the shape of the real status.io page of Hornetsecurity (api.status.io/1.0/status/&lt;page&gt; and its RSS).
/// </summary>
public sealed class StatusFeedTests
{
    private const string StatusIoJson = """
        {
          "result": {
            "status_overall": { "status": "Partial Service Disruption", "status_code": 400 },
            "status": [
              { "id": "1", "name": "365 Permission Manager", "status": "Operational", "status_code": 100,
                "containers": [
                  { "name": "Germany - Frankfurt", "status": "Operational", "status_code": 100 },
                  { "name": "United States - Georgia", "status": "Operational", "status_code": 100 } ] },
              { "id": "2", "name": "Spam and Malware Protection", "status": "Partial Service Disruption", "status_code": 400,
                "containers": [ { "name": "Germany - Frankfurt", "status": "Partial Service Disruption", "status_code": 400 } ] },
              { "id": "3", "name": "Support Hotline", "status": "Service Disruption", "status_code": 500, "containers": [] }
            ]
          }
        }
        """;

    private static async Task<SensorExecutionResult> RunHttpAdvancedAsync(string body, params (string Key, string Value)[] parameters)
    {
        var executor = new HttpAdvancedSensorExecutor(new HttpClient(new FixedAnswer(body, "application/json")));
        var settings = new MonitoringSettings();
        foreach (var (key, value) in parameters)
        {
            settings.Parameters[key] = value;
        }

        return await executor.ExecuteAsync(new SensorExecutionContext("http-advanced", "https://api.status.io/1.0/status/x", settings));
    }

    [Fact]
    public async Task A_list_walked_with_a_wildcard_gives_one_channel_per_service_named_after_it()
    {
        var result = await RunHttpAdvancedAsync(StatusIoJson, ("extractMode", "json"), ("jsonPaths", "result.status[*].status|{name}"));

        var services = result.Channels.Where(channel => channel.Message is not null).ToArray();
        Assert.Equal(["365 Permission Manager", "Spam and Malware Protection", "Support Hotline"], services.Select(channel => channel.Label));
        Assert.Equal([SensorState.Healthy, SensorState.Warning, SensorState.Critical], services.Select(channel => channel.State!.Value));
        Assert.Equal([0d, 1d, 2d], services.Select(channel => channel.Value!.Value));
        Assert.Equal("365_permission_manager", services[0].Key);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("2 of 3 not OK", result.Message);
        Assert.Contains("Support Hotline: Service Disruption", result.Message);
    }

    [Fact]
    public async Task Nested_lists_read_the_outer_name_with_dot_dot_and_numbers_stay_numbers()
    {
        var result = await RunHttpAdvancedAsync(StatusIoJson, ("extractMode", "json"),
            ("jsonPaths", "result.status[*].containers[*].status_code|{../name} - {name}"));

        var labels = result.Channels.Select(channel => channel.Label).ToArray();
        Assert.Contains("365 Permission Manager - Germany - Frankfurt", labels);
        Assert.Contains("Spam and Malware Protection - Germany - Frankfurt", labels);
        Assert.Equal(400, result.Channels.Single(channel => channel.Label == "Spam and Malware Protection - Germany - Frankfurt").Value);
        // Numbers carry no state of their own: the sensor stays as healthy as the HTTP answer.
        Assert.Equal(SensorState.Healthy, result.State);
    }

    [Fact]
    public async Task All_services_fine_says_how_many_and_old_paths_keep_working()
    {
        var allFine = StatusIoJson.Replace("Partial Service Disruption", "Operational").Replace("\"Service Disruption\"", "\"Operational\"");
        var result = await RunHttpAdvancedAsync(allFine, ("extractMode", "json"),
            ("jsonPaths", "result.status[*].status|{name}\nresult.status|Services\nresult.status_overall.status_code|Overall"));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("3 OK", result.Message);
        Assert.Equal(3, result.Channels.Single(channel => channel.Key == "services").Value);
        Assert.Equal(400, result.Channels.Single(channel => channel.Key == "overall").Value);
    }

    [Fact]
    public async Task A_label_without_placeholders_numbers_the_items_and_duplicate_names_get_their_own_keys()
    {
        var json = """{ "items": [ { "name": "A", "v": 1 }, { "name": "A", "v": 2 } ] }""";
        var numbered = await RunHttpAdvancedAsync(json, ("extractMode", "json"), ("jsonPaths", "items[*].v|Value"));
        Assert.Equal(["Value 1", "Value 2"], numbered.Channels.Where(channel => channel.Label.StartsWith("Value")).Select(channel => channel.Label));

        var named = await RunHttpAdvancedAsync(json, ("extractMode", "json"), ("jsonPaths", "items[*].v|{name}"));
        Assert.Equal(["a", "a_2"], named.Channels.Where(channel => channel.Label == "A").Select(channel => channel.Key));
    }

    [Theory]
    [InlineData("Operational", SensorState.Healthy)]
    [InlineData("All Systems Operational", SensorState.Healthy)]
    [InlineData("none", SensorState.Healthy)]
    [InlineData("Planned Maintenance", SensorState.Warning)]
    [InlineData("under_maintenance", SensorState.Warning)]
    [InlineData("Degraded Performance", SensorState.Warning)]
    [InlineData("partial_outage", SensorState.Warning)]
    [InlineData("Partial Service Disruption", SensorState.Warning)]
    [InlineData("major_outage", SensorState.Critical)]
    [InlineData("Service Disruption", SensorState.Critical)]
    [InlineData("Security Event", SensorState.Critical)]
    [InlineData("down", SensorState.Critical)]
    public void Status_texts_of_the_common_status_pages_are_understood(string text, SensorState expected) =>
        Assert.Equal(expected, StatusText.Classify(text));

    [Theory]
    [InlineData("Frankfurt")]
    [InlineData("")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    public void A_text_that_is_no_status_is_not_guessed(string text) => Assert.Null(StatusText.Classify(text));

    private const string StatusFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel>
          <title><![CDATA[Hornetsecurity - Service Status Page]]></title>
          <item><title><![CDATA[[HSE20261104-01-I] Incident: Delayed Mail Delivery Germany]]></title>
            <description><![CDATA[<small>November 4, 2026 09:10 CET</small><br /><b>Investigating</b> - Mails to Germany are delayed.<br /><br /><small>November 4, 2026 09:40 CET</small><br /><b>Identified</b> - A queue is stuck.]]></description>
            <pubDate>Wed, 04 Nov 2026 08:40:00 GMT</pubDate></item>
          <item><title><![CDATA[[HSE20261103-01-M] Scheduled Maintenance: Support Hotline Germany]]></title>
            <description><![CDATA[<b>Scheduled</b> - Maintenance.<br /><b>Active</b> - Underway.]]></description>
            <pubDate>Wed, 04 Nov 2026 07:00:00 GMT</pubDate></item>
          <item><title><![CDATA[[HSE20261027-01-I] Incident: Partial Service Disruption: German Service Hotline]]></title>
            <description><![CDATA[<b>Investigating</b> - Disrupted.<br /><b>Monitoring</b> - Better.<br /><b>Resolved</b> - Fixed.]]></description>
            <pubDate>Tue, 27 Oct 2026 10:12:00 +0100</pubDate></item>
          <item><title><![CDATA[[HSE20261006-01-I] Incident: Old one never closed]]></title>
            <description><![CDATA[<b>Investigating</b> - Long ago.]]></description>
            <pubDate>Tue, 06 Oct 2026 10:00:00 GMT</pubDate></item>
        </channel></rss>
        """;

    private static RssFeedSensorExecutor.Feed ParseFeed(string xml) =>
        RssFeedSensorExecutor.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml.Trim())));

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_status_page_feed_counts_the_incident_that_is_not_resolved_and_the_maintenance_under_way()
    {
        var feed = ParseFeed(StatusFeed);
        Assert.Equal("Hornetsecurity - Service Status Page", feed.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 9, 12, 0, TimeSpan.Zero), feed.Items[2].Published);

        var result = RssFeedSensorExecutor.Evaluate(feed, new MonitoringSettings(), Now, TimeSpan.Zero);

        Assert.Equal(SensorState.Warning, result.State);
        Assert.Equal(1, result.Channels.Single(channel => channel.Key == "openIncidents").Value);
        Assert.Equal(1, result.Channels.Single(channel => channel.Key == "maintenanceUnderway").Value);
        Assert.Equal(2, result.Channels.Single(channel => channel.Key == "newItems").Value);
        Assert.Equal(0.3, result.Channels.Single(channel => channel.Key == "newestAgeHours").Value);
        Assert.Contains("Delayed Mail Delivery Germany", result.Message);
        Assert.DoesNotContain("Old one never closed", result.Message);
    }

    [Fact]
    public void Once_resolved_the_feed_is_healthy_and_a_watched_phrase_still_warns()
    {
        var resolved = StatusFeed.Replace("<b>Identified</b> - A queue is stuck.", "<b>Resolved</b> - Delivered.");
        var feed = ParseFeed(resolved);

        var quiet = RssFeedSensorExecutor.Evaluate(feed, new MonitoringSettings(), Now, TimeSpan.Zero);
        Assert.Equal(SensorState.Healthy, quiet.State);
        Assert.Contains("no open incidents", quiet.Message);
        Assert.Contains("maintenance under way", quiet.Message);

        var watching = new MonitoringSettings();
        watching.Parameters["rss.match"] = "mail delivery, Austria";
        var watched = RssFeedSensorExecutor.Evaluate(feed, watching, Now, TimeSpan.Zero);
        Assert.Equal(SensorState.Warning, watched.State);
        Assert.Equal(1, watched.Channels.Single(channel => channel.Key == "matches").Value);
    }

    [Fact]
    public void An_atom_feed_is_read_too_and_without_phases_it_only_counts()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Release notes</title>
              <entry><title>Version 4.2</title><summary>Investigating new features</summary><updated>2026-11-04T06:00:00Z</updated></entry>
              <entry><title>Version 4.1</title><summary>Bug fixes</summary><updated>2026-10-01T06:00:00Z</updated></entry>
            </feed>
            """;
        var feed = ParseFeed(atom);
        Assert.Equal(2, feed.Items.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 6, 0, 0, TimeSpan.Zero), feed.Items[0].Published);

        var settings = new MonitoringSettings();
        settings.Parameters["rss.statusPage"] = "false";
        var result = RssFeedSensorExecutor.Evaluate(feed, settings, Now, TimeSpan.Zero);
        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(1, result.Channels.Single(channel => channel.Key == "newItems").Value);
        Assert.Equal(0, result.Channels.Single(channel => channel.Key == "openIncidents").Value);
    }

    [Fact]
    public async Task Something_that_is_not_a_feed_is_critical_in_words()
    {
        var executor = new RssFeedSensorExecutor(() => new FixedAnswer("<html><body>hi</body></html>", "text/html"));
        var result = await executor.ExecuteAsync(new SensorExecutionContext("rss-feed", "https://example.com/rss", new MonitoringSettings()));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("neither <rss> nor <feed>", result.Message);
    }

    private sealed class FixedAnswer(string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
    }
}
