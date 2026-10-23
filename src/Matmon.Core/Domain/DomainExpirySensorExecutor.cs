using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Matmon.Core.Domain;

/// <summary>
/// When a domain runs out - read from the registry, not from DNS. RDAP first (the JSON successor of WHOIS; IANA's
/// bootstrap file says which server answers for a TLD), and for a TLD without RDAP (.de, .eu, .io and other ccTLDs)
/// WHOIS on port 43, the server again named by IANA. A lapsed domain takes the website AND the mail down at once, which
/// is why it is worth a sensor of its own.
/// <para>Not every registry publishes an expiry date: DENIC (.de) and EURid (.eu) never do. Then the sensor checks that
/// the domain is still registered and says why there is no date. A domain in redemption / pending delete or on hold
/// (not in DNS) is critical whatever the date says.</para>
/// </summary>
public sealed class DomainExpirySensorExecutor : ISensorExecutor
{
    public const string DaysChannel = "daysUntilExpiry";
    public const string RegisteredChannel = "registered";

    private const string BootstrapUrl = "https://data.iana.org/rdap/dns.json";
    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim BootstrapGate = new(1, 1);
    private static (DateTimeOffset Loaded, Dictionary<string, string> Servers)? _sharedBootstrap;

    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Func<string, string, CancellationToken, Task<string>> _whois;
    private readonly bool _sharedCache;

    public DomainExpirySensorExecutor()
        : this(() => new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }, QueryWhoisAsync)
    {
        _sharedCache = true;
    }

    /// <summary>For tests: the HTTP handler (RDAP + bootstrap) and the WHOIS query (server, query → answer).</summary>
    public DomainExpirySensorExecutor(Func<HttpMessageHandler> handlerFactory, Func<string, string, CancellationToken, Task<string>> whois)
    {
        _handlerFactory = handlerFactory;
        _whois = whois;
    }

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "domain-expiry",
        DisplayName = "Domain expiry",
        Description = "When a domain runs out, read from its registry (RDAP, else WHOIS): days until expiry, and whether it is still registered and not on hold. Some registries (.de, .eu) publish no date - then only the registration is checked.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "domain.name",
                Label = "Domain",
                Kind = SensorParameterKind.Text,
                Description = "The registered domain, e.g. example.com. Empty = the target (a leading www. and any sub-domain the registry does not know are dropped).",
                Placeholder = "example.com"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var configured = MonitoringSettings.TryReadParameter(context.Settings, "domain.name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : context.Target;
        if (NormalizeDomain(configured) is not { } domain)
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, string.IsNullOrWhiteSpace(configured)
                ? "Which domain? Enter it, or give the sensor a target."
                : $"'{configured}' is not a domain name.");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(15);
        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Matmon");
        try
        {
            var tld = domain[(domain.LastIndexOf('.') + 1)..];
            var servers = await BootstrapAsync(client, timeoutCts.Token);
            var record = servers.TryGetValue(tld, out var rdapBase)
                ? await LookupRdapAsync(client, rdapBase, domain, timeoutCts.Token)
                : await LookupWhoisAsync(domain, tld, timeoutCts.Token);
            watch.Stop();
            var result = BuildResult(record, DateTimeOffset.UtcNow, watch.Elapsed);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The registry did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException or JsonException)
        {
            return SensorExecutionResult.Unknown($"The registry cannot be asked right now: {exception.Message}");
        }
    }

    // ---- the record (pure) -----------------------------------------------------------------------------------------------

    public enum DomainSource { Rdap, Whois, None }

    /// <summary>What the registry said. <see cref="Registered"/> null = it could not be asked at all (no RDAP, no WHOIS).</summary>
    public sealed record DomainRecord(
        string Domain,
        bool? Registered,
        DateTimeOffset? Expires,
        IReadOnlyList<string> Statuses,
        string? Registrar,
        DomainSource Source,
        string? Note = null);

    public static SensorExecutionResult BuildResult(DomainRecord record, DateTimeOffset now, TimeSpan duration)
    {
        if (record.Registered is null)
        {
            return SensorExecutionResult.Unknown(record.Note ?? $"The registry of '{record.Domain}' cannot be asked.");
        }

        var channels = new List<SensorChannelValue>
        {
            new() { Key = RegisteredChannel, Label = "Registered", Value = record.Registered.Value ? 1 : 0, MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false }
        };
        if (!record.Registered.Value)
        {
            return SensorExecutionResult.Critical(duration, $"'{record.Domain}' is not registered - the registry reports it as free.", 0, RegisteredChannel,
                channels.Select(channel => channel with { IsDefault = true }).ToList());
        }

        double? days = record.Expires is { } expires ? Math.Floor((expires - now).TotalDays) : null;
        if (days is { } remaining)
        {
            channels.Insert(0, new SensorChannelValue { Key = DaysChannel, Label = "Days until expiry", Value = remaining, Unit = "d", MeasurementKind = SensorMeasurementKind.Count });
        }

        var defaultKey = days is null ? RegisteredChannel : DaysChannel;
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultKey }).ToList();
        var value = channels.First(channel => channel.IsDefault).Value;

        var builder = new StringBuilder(record.Domain);
        if (record.Expires is { } date)
        {
            builder.Append(days < 0 ? " EXPIRED on " : " expires on ").Append(date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append(days < 0 ? $" ({-days} days ago)" : $" (in {days} day{(days == 1 ? string.Empty : "s")})");
        }
        else
        {
            builder.Append(" is registered - ").Append(record.Note ?? "the registry publishes no expiry date, so only the registration is checked");
        }

        if (!string.IsNullOrWhiteSpace(record.Registrar))
        {
            builder.Append(", registrar ").Append(record.Registrar.Trim());
        }

        var trouble = record.Statuses.Where(IsCriticalStatus).ToList();
        var attention = record.Statuses.Where(status => !IsCriticalStatus(status) && IsWarningStatus(status)).ToList();
        if (trouble.Count + attention.Count > 0)
        {
            builder.Append(" - status: ").Append(string.Join(", ", trouble.Concat(attention)));
        }

        var message = builder.ToString();
        if (days < 0 || trouble.Count > 0)
        {
            return SensorExecutionResult.Critical(duration, message, value, defaultKey, channels);
        }

        return attention.Count > 0
            ? SensorExecutionResult.Warning(duration, message, value, defaultKey, channels)
            : SensorExecutionResult.Healthy(duration, message, value, defaultKey, channels);
    }

    /// <summary>EPP / RDAP statuses that mean the domain is about to go or is already out of DNS.</summary>
    private static bool IsCriticalStatus(string status) =>
        Compact(status) is "redemptionperiod" or "pendingdelete" or "serverhold" or "clienthold" or "pendingrestore";

    private static bool IsWarningStatus(string status) =>
        Compact(status) is "inactive" or "pendingtransfer" or "autorenewperiod" or "transferperiod";

    private static string Compact(string status) =>
        new(status.Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());

    // ---- RDAP ------------------------------------------------------------------------------------------------------------

    private async Task<Dictionary<string, string>> BootstrapAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (_sharedCache && _sharedBootstrap is { } cached && DateTimeOffset.UtcNow - cached.Loaded < BootstrapLifetime)
        {
            return cached.Servers;
        }

        await BootstrapGate.WaitAsync(cancellationToken);
        try
        {
            if (_sharedCache && _sharedBootstrap is { } again && DateTimeOffset.UtcNow - again.Loaded < BootstrapLifetime)
            {
                return again.Servers;
            }

            using var response = await client.GetAsync(BootstrapUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var servers = ParseBootstrap(await response.Content.ReadAsStringAsync(cancellationToken));
            if (_sharedCache)
            {
                _sharedBootstrap = (DateTimeOffset.UtcNow, servers);
            }

            return servers;
        }
        finally
        {
            BootstrapGate.Release();
        }
    }

    /// <summary>IANA's bootstrap: services = [[[tld, ...], [url, ...]], ...]; the first https URL per TLD.</summary>
    public static Dictionary<string, string> ParseBootstrap(string json)
    {
        var servers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
        {
            return servers;
        }

        foreach (var service in services.EnumerateArray())
        {
            if (service.ValueKind != JsonValueKind.Array || service.GetArrayLength() < 2)
            {
                continue;
            }

            var urls = service[1].EnumerateArray().Select(url => url.GetString()).OfType<string>().ToList();
            var url = urls.FirstOrDefault(candidate => candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
            if (url is null)
            {
                continue;
            }

            foreach (var tld in service[0].EnumerateArray().Select(entry => entry.GetString()).OfType<string>())
            {
                servers.TryAdd(tld, url.EndsWith('/') ? url : url + "/");
            }
        }

        return servers;
    }

    private static async Task<DomainRecord> LookupRdapAsync(HttpClient client, string baseUrl, string domain, CancellationToken cancellationToken)
    {
        // A registry knows the registered name, not www.shop.example.com - drop labels from the left until it answers.
        for (var name = domain; name.Count(character => character == '.') >= 1; name = name[(name.IndexOf('.') + 1)..])
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "domain/" + name);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rdap+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
            return ParseRdap(name, await response.Content.ReadAsStringAsync(cancellationToken));
        }

        return new DomainRecord(domain, false, null, [], null, DomainSource.Rdap);
    }

    public static DomainRecord ParseRdap(string domain, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in events.EnumerateArray())
            {
                if (entry.TryGetProperty("eventAction", out var action) && action.GetString() is "expiration"
                    && entry.TryGetProperty("eventDate", out var date)
                    && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    expires = parsed;
                }
            }
        }

        var statuses = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Array
            ? status.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToList()
            : [];
        var name = root.TryGetProperty("ldhName", out var ldh) && ldh.GetString() is { Length: > 0 } ldhName ? ldhName.ToLowerInvariant() : domain;
        return new DomainRecord(name, true, expires, statuses, RdapRegistrar(root), DomainSource.Rdap,
            expires is null ? "the registry's RDAP answer carries no expiry date, so only the registration is checked" : null);
    }

    private static string? RdapRegistrar(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entity in entities.EnumerateArray())
        {
            if (!entity.TryGetProperty("roles", out var roles) || !roles.EnumerateArray().Any(role => role.GetString() == "registrar")
                || !entity.TryGetProperty("vcardArray", out var vcard) || vcard.ValueKind != JsonValueKind.Array || vcard.GetArrayLength() < 2)
            {
                continue;
            }

            foreach (var property in vcard[1].EnumerateArray())
            {
                if (property.ValueKind == JsonValueKind.Array && property.GetArrayLength() >= 4 && property[0].GetString() == "fn")
                {
                    return property[3].GetString();
                }
            }
        }

        return null;
    }

    // ---- WHOIS -----------------------------------------------------------------------------------------------------------

    private async Task<DomainRecord> LookupWhoisAsync(string domain, string tld, CancellationToken cancellationToken)
    {
        var referral = await _whois("whois.iana.org", tld, cancellationToken);
        var server = Regex.Match(referral, @"^\s*whois:\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase) is { Success: true } match ? match.Groups[1].Value : null;
        if (server is null)
        {
            return new DomainRecord(domain, null, null, [], null, DomainSource.None, $"The .{tld} registry offers neither RDAP nor WHOIS.");
        }

        // Like RDAP: a sub-domain is unknown to the registry, so drop labels from the left while it says "not found".
        DomainRecord? last = null;
        for (var name = domain; name.Contains('.'); name = name[(name.IndexOf('.') + 1)..])
        {
            last = ParseWhois(name, tld, await _whois(server, name, cancellationToken));
            if (last.Registered != false || !name[(name.IndexOf('.') + 1)..].Contains('.'))
            {
                return last;
            }
        }

        return last ?? new DomainRecord(domain, false, null, [], null, DomainSource.Whois);
    }

    private static readonly Regex NotFound = new(
        @"^\s*(status:\s*(free|available)\b|no match\b|not found\b|no data found|no entries found|domain not found|object does not exist|.*is available for registration)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExpiryLine = new(
        @"^\s*(registry expiry date|registrar registration expiration date|expiry date|expiration date|expires on|expires|expire|expiration time|paid-till|renewal date|valid until)\s*:\s*(?<date>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StatusLine = new(@"^\s*(domain\s+)?status:\s*(?<status>[A-Za-z][A-Za-z ]*?)(\s+https?://\S+)?\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RegistrarLine = new(@"^\s*registrar:\s*(?<name>\S.*?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
        "dd-MMM-yyyy", "dd-MMM-yyyy HH:mm:ss", "yyyy.MM.dd", "yyyy.MM.dd HH:mm:ss", "dd.MM.yyyy", "dd.MM.yyyy HH:mm:ss", "yyyy/MM/dd", "dd/MM/yyyy",
        "ddd MMM dd HH:mm:ss 'GMT' yyyy"
    ];

    public static DomainRecord ParseWhois(string domain, string tld, string answer)
    {
        if (NotFound.IsMatch(answer))
        {
            return new DomainRecord(domain, false, null, [], null, DomainSource.Whois);
        }

        DateTimeOffset? expires = null;
        foreach (Match match in ExpiryLine.Matches(answer))
        {
            if (ParseDate(match.Groups["date"].Value) is { } parsed)
            {
                expires = parsed;
                break;
            }
        }

        var statuses = StatusLine.Matches(answer).Select(match => match.Groups["status"].Value.Trim()).Where(status => status.Length > 0).Distinct().ToList();
        var registrar = RegistrarLine.Match(answer) is { Success: true } registrarMatch ? registrarMatch.Groups["name"].Value : null;
        var mentionsDomain = answer.Contains(domain, StringComparison.OrdinalIgnoreCase);
        if (expires is null && statuses.Count == 0 && !mentionsDomain)
        {
            return new DomainRecord(domain, null, null, [], null, DomainSource.Whois, $"The .{tld} WHOIS answer could not be read.");
        }

        return new DomainRecord(domain, true, expires, statuses.Where(status => !status.Equals("connect", StringComparison.OrdinalIgnoreCase)).ToList(), registrar, DomainSource.Whois,
            expires is null ? $"the .{tld} registry publishes no expiry date, so only the registration is checked" : null);
    }

    private static DateTimeOffset? ParseDate(string text)
    {
        var value = text.Trim().TrimEnd('.');
        if (DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            return exact;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loose) ? loose : null;
    }

    private static async Task<string> QueryWhoisAsync(string server, string query, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server, 43, cancellationToken);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(query + "\r\n"), cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length < 256 * 1024)
        {
            buffer.Write(chunk, 0, read);
        }

        // WHOIS has no charset; UTF-8 where it is valid, Latin-1 where it is not.
        var bytes = buffer.ToArray();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>A host, URL or name → the lower-case ASCII (punycode) domain without a leading www.; null when it is no
    /// domain (an IP address, a single label).</summary>
    public static string? NormalizeDomain(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            text = uri.Host;
        }
        else
        {
            text = text.Split('/', '?', '#')[0];
            if (text.Count(character => character == ':') == 1)
            {
                text = text.Split(':')[0];
            }
        }

        text = text.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.StartsWith("www.", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (IPAddress.TryParse(text, out _) || !text.Contains('.'))
        {
            return null;
        }

        try
        {
            text = new IdnMapping().GetAscii(text);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return Regex.IsMatch(text, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$") ? text : null;
    }
}
