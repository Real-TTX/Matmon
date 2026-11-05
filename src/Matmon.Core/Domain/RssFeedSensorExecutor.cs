using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Matmon.Core.Domain;

/// <summary>
/// An RSS / Atom feed - most often a provider's status page ("Hornetsecurity - Service Status Page", status.io,
/// Atlassian Statuspage, Better Uptime ...), which publishes every incident and maintenance as an item and adds an update
/// to it until it is resolved. The sensor counts what is new and, for such feeds, which incidents are still OPEN: an item
/// whose updates name phases (Investigating, Identified, Monitoring ...) but no end (Resolved, Completed ...). An open
/// incident is a warning; a maintenance that is under way is reported, not alarmed (it was announced). A phrase can be
/// watched too ("Germany", "Mail Filtering") - a new item that names it is a warning.
/// </summary>
public sealed class RssFeedSensorExecutor : ISensorExecutor
{
    public const string OpenIncidentsChannel = "openIncidents";

    private readonly Func<HttpMessageHandler> _handlerFactory;

    public RssFeedSensorExecutor()
        : this(() => new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.All })
    {
    }

    /// <summary>For tests: the handler the feed is fetched through.</summary>
    public RssFeedSensorExecutor(Func<HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory;
    }

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "rss-feed",
        DisplayName = "RSS / Atom feed",
        Description = "Reads an RSS or Atom feed - typically a provider's status page: open incidents (warning), maintenance under way, new items, how old the newest one is, and items that name a phrase you watch.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "rss.window",
                Label = "New items within (hours)",
                Kind = SensorParameterKind.Integer,
                Description = "An item published within this many hours counts as new.",
                DefaultValue = "24"
            },
            new SensorParameterDefinition
            {
                Key = "rss.statusPage",
                Label = "Read incident phases",
                Kind = SensorParameterKind.Boolean,
                Description = "For a status page feed: an item that names phases (Investigating, Identified, Monitoring ...) but no end (Resolved, Completed) is an open incident.",
                DefaultValue = "true"
            },
            new SensorParameterDefinition
            {
                Key = "rss.openDays",
                Label = "Open incidents of the last (days)",
                Kind = SensorParameterKind.Integer,
                Description = "Older items are not counted as open, even without an end - a provider that never closed one should not alarm for ever.",
                DefaultValue = "14"
            },
            new SensorParameterDefinition
            {
                Key = "rss.match",
                Label = "Watch for",
                Kind = SensorParameterKind.Text,
                Description = "Optional: a new item whose title or text contains one of these phrases (separated by commas) is a warning.",
                Placeholder = "Germany, Mail Filtering"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(context.Target?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "target must be the feed's address (http or https)");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.Settings.Timeout ?? TimeSpan.FromSeconds(15));
            using var client = new HttpClient(_handlerFactory());
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Matmon-RSS/1.0");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, $"the feed answered http {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var feed = Parse(stream);
            watch.Stop();
            return Evaluate(feed, context.Settings, DateTimeOffset.UtcNow, watch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, "the feed did not answer in time");
        }
        catch (XmlException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"not an RSS or Atom feed: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.Message);
        }
    }

    public sealed record FeedItem(string Title, string Text, DateTimeOffset? Published);

    public sealed record Feed(string Title, IReadOnlyList<FeedItem> Items);

    /// <summary>RSS 2.0, RSS 1.0 (RDF) and Atom. No DTDs, no external entities, at most 16 MB.</summary>
    public static Feed Parse(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new XmlException("the document is empty");

        static string Value(XElement parent, params string[] names) =>
            parent.Elements().FirstOrDefault(element => names.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;

        if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
        {
            var entries = root.Elements().Where(element => element.Name.LocalName == "entry")
                .Select(entry => new FeedItem(
                    Value(entry, "title"),
                    Value(entry, "content", "summary"),
                    ParseDate(Value(entry, "updated", "published"))))
                .ToArray();
            return new Feed(Value(root, "title"), entries);
        }

        if (root.Name.LocalName is not ("rss" or "RDF"))
        {
            throw new XmlException($"<{root.Name.LocalName}> is neither <rss> nor <feed>");
        }

        var channel = root.Elements().FirstOrDefault(element => element.Name.LocalName == "channel") ?? root;
        var items = root.Descendants().Where(element => element.Name.LocalName == "item")
            .Select(item => new FeedItem(
                Value(item, "title"),
                // content:encoded carries the full text where description is only a teaser.
                item.Elements().FirstOrDefault(element => element.Name.LocalName == "encoded")?.Value.Trim() is { Length: > 0 } encoded
                    ? encoded
                    : Value(item, "description"),
                ParseDate(Value(item, "pubDate", "date", "updated"))))
            .ToArray();
        return new Feed(Value(channel, "title"), items);
    }

    private static DateTimeOffset? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // RFC 822 ("Tue, 03 Nov 2026 11:31:29 GMT", "... +0100") as RSS writes it, else ISO 8601 as Atom does.
        var zone = Regex.Replace(text.Trim(), @"\s(GMT|UTC|UT|Z|EST|EDT|CST|CDT|MST|MDT|PST|PDT)$", match => match.Groups[1].Value switch
        {
            "EST" => " -05:00", "EDT" => " -04:00", "CST" => " -06:00", "CDT" => " -05:00",
            "MST" => " -07:00", "MDT" => " -06:00", "PST" => " -08:00", "PDT" => " -07:00", _ => " +00:00"
        }, RegexOptions.None, TimeSpan.FromSeconds(1));
        zone = Regex.Replace(zone, @"\s([+-]\d{2})(\d{2})$", " $1:$2", RegexOptions.None, TimeSpan.FromSeconds(1));
        string[] formats = ["ddd, d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz", "d MMM yyyy HH:mm:ss zzz"];
        if (DateTimeOffset.TryParseExact(zone, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var rfc))
        {
            return rfc.ToUniversalTime();
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso)
            ? iso.ToUniversalTime()
            : null;
    }

    // Phases status pages name in an update. An end means the item is closed whatever came before.
    private static readonly string[] IncidentPhases = ["investigating", "identified", "monitoring", "verifying"];
    private static readonly string[] EndPhases = ["resolved", "completed", "closed", "postmortem", "post-mortem", "cancelled", "canceled"];

    public enum ItemKind { Other, Incident, Maintenance }

    public sealed record ItemPhase(ItemKind Kind, bool Open, bool Underway);

    /// <summary>What a status page item is and whether it is still open. Tags are stripped; the phases are looked for as
    /// words, so "Resolved" in "the cause is not resolved yet" can close an item - status pages write the phase as a label
    /// of its own, which is what this reads.</summary>
    public static ItemPhase Classify(FeedItem item)
    {
        var text = Regex.Replace(item.Text, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = WebUtility.HtmlDecode(text).ToLowerInvariant();
        var title = item.Title.ToLowerInvariant();
        bool Has(string phrase) => Regex.IsMatch(text, $@"(^|\W){Regex.Escape(phrase)}(\W|$)", RegexOptions.None, TimeSpan.FromSeconds(1));

        var ended = EndPhases.Any(Has);
        if (title.Contains("maintenance"))
        {
            return new ItemPhase(ItemKind.Maintenance, !ended, !ended && (Has("active") || Has("in progress")));
        }

        var incident = IncidentPhases.Any(Has) || title.Contains("incident") || title.Contains("outage") ||
            title.Contains("disruption") || title.Contains("degraded");
        return incident ? new ItemPhase(ItemKind.Incident, !ended, false) : new ItemPhase(ItemKind.Other, false, false);
    }

    public static SensorExecutionResult Evaluate(Feed feed, MonitoringSettings settings, DateTimeOffset now, TimeSpan elapsed)
    {
        var windowHours = ReadNumber(settings, "rss.window", 24, 1, 24 * 90);
        var openDays = ReadNumber(settings, "rss.openDays", 14, 1, 365);
        var readPhases = !MonitoringSettings.TryReadParameter(settings, "rss.statusPage", out var phasesRaw) ||
            !bool.TryParse(phasesRaw, out var phasesOn) || phasesOn;
        var phrases = MonitoringSettings.TryReadParameter(settings, "rss.match", out var matchRaw)
            ? matchRaw.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        // An item dated in the future is an announcement (Statuspage dates a scheduled maintenance by its start): it is neither "new"
        // nor the newest item - counted as new, it made every page with planned work look busy, with a negative age.
        var past = feed.Items.Where(item => item.Published is { } published && published <= now + TimeSpan.FromMinutes(5)).ToArray();
        var newItems = past.Where(item => now - item.Published!.Value <= TimeSpan.FromHours(windowHours)).ToArray();
        var newest = past.Select(item => item.Published!.Value).DefaultIfEmpty().Max();

        var open = new List<FeedItem>();
        var underway = new List<FeedItem>();
        if (readPhases)
        {
            foreach (var item in feed.Items.Where(item => item.Published is null || now - item.Published.Value <= TimeSpan.FromDays(openDays)))
            {
                var phase = Classify(item);
                if (phase.Kind == ItemKind.Incident && phase.Open)
                {
                    open.Add(item);
                }
                else if (phase.Kind == ItemKind.Maintenance && phase.Underway)
                {
                    underway.Add(item);
                }
            }
        }

        var matches = phrases.Length == 0
            ? []
            : newItems.Where(item => phrases.Any(phrase =>
                item.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase) ||
                item.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase))).ToArray();

        var channels = new List<SensorChannelValue>
        {
            new() { Key = OpenIncidentsChannel, Label = "Open incidents", Value = open.Count, MeasurementKind = SensorMeasurementKind.Count, IsDefault = true },
            new() { Key = "maintenanceUnderway", Label = "Maintenance under way", Value = underway.Count, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = "newItems", Label = "New items", Value = newItems.Length, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = "items", Label = "Items in the feed", Value = feed.Items.Count, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false }
        };
        if (newest != default)
        {
            channels.Add(new() { Key = "newestAgeHours", Label = "Newest item age", Value = Math.Round((now - newest).TotalHours, 1), Unit = "h", MeasurementKind = SensorMeasurementKind.Duration });
        }

        if (phrases.Length > 0)
        {
            channels.Add(new() { Key = "matches", Label = "Items naming a watched phrase", Value = matches.Length, MeasurementKind = SensorMeasurementKind.Count });
        }

        var state = open.Count > 0 || matches.Length > 0 ? SensorState.Warning : SensorState.Healthy;
        var parts = new List<string>();
        if (open.Count > 0)
        {
            parts.Add($"{open.Count} open incident{(open.Count == 1 ? string.Empty : "s")}: {string.Join("; ", open.Take(2).Select(item => Shorten(item.Title)))}{(open.Count > 2 ? " ..." : string.Empty)}");
        }
        else if (readPhases && underway.Count > 0)
        {
            parts.Add("no open incidents");
        }

        if (matches.Length > 0)
        {
            parts.Add($"{matches.Length} new item{(matches.Length == 1 ? string.Empty : "s")} naming a watched phrase: {Shorten(matches[0].Title)}");
        }

        if (underway.Count > 0)
        {
            parts.Add($"maintenance under way: {Shorten(underway[0].Title)}");
        }

        if (parts.Count == 0)
        {
            parts.Add(readPhases ? "no open incidents" : $"{feed.Items.Count} items");
            if (newest != default)
            {
                parts.Add($"newest {Age(now - newest)} ago");
            }
        }

        var result = new SensorExecutionResult(state, elapsed, open.Count, string.Join(" · ", parts))
        {
            DefaultChannelKey = OpenIncidentsChannel,
            Channels = channels
        };
        return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
    }

    private static double ReadNumber(MonitoringSettings settings, string key, double fallback, double min, double max) =>
        MonitoringSettings.TryReadParameter(settings, key, out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static string Age(TimeSpan span) =>
        span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min"
        : span.TotalDays < 2 ? $"{(int)span.TotalHours} h"
        : $"{(int)span.TotalDays} d";

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 90 ? trimmed : trimmed[..90] + "...";
    }
}
