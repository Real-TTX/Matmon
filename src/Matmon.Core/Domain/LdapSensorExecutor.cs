using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Matmon.Core.Domain;

/// <summary>
/// A directory server - Active Directory, OpenLDAP, Samba, FreeIPA, Synology Directory Server - over LDAPv3, with a client
/// of its own on top of the SNMP code's BER helpers (no native libldap, which neither the Docker image nor an agent can be
/// relied on to have): it reads the root DSE, binds (simple bind with a user, else anonymous) and optionally searches,
/// over plain LDAP, LDAPS or StartTLS. It says how long each took and how many entries the search found.
/// <para>On Active Directory it also reports the clock skew (the DC's <c>currentTime</c> against the probe's clock -
/// Kerberos refuses logons beyond five minutes) and whether the DC is synchronized, and spells out the AD reason behind
/// a refused bind (wrong password, account locked / disabled / expired, password expired).</para>
/// </summary>
public sealed class LdapSensorExecutor : ISensorExecutor
{
    public const string ResponseTimeChannel = "responseTime";
    public const string ClockSkewChannel = "clockSkewSeconds";
    public const int SearchSizeLimit = 1000;

    private static readonly string[] RootDseAttributes = ["namingContexts", "defaultNamingContext", "dnsHostName", "currentTime", "isSynchronized", "vendorName", "vendorVersion", "domainControllerFunctionality"];

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "ldap",
        DisplayName = "LDAP / Active Directory",
        Description = "A directory server (Active Directory, OpenLDAP, Samba, FreeIPA …): bind, optional search with entry count, response time; on Active Directory also clock skew and sync state. Plain LDAP, LDAPS or StartTLS.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "generic.username",
                Label = "Bind user (optional)",
                Kind = SensorParameterKind.Text,
                Description = "A DN (cn=monitor,dc=example,dc=com) or, on Active Directory, monitor@corp.example. Empty = anonymous (only the root DSE is readable then on most servers).",
                Placeholder = "monitor@corp.example",
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "generic.password",
                Label = "Password",
                Kind = SensorParameterKind.Secret,
                Description = "The bind user's password.",
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "ldap.security",
                Label = "Connection",
                Kind = SensorParameterKind.ValueList,
                Description = "Active Directory refuses a password over plain LDAP when 'LDAP signing' is required - use LDAPS or StartTLS there.",
                DefaultValue = "none",
                Options =
                [
                    new SensorParameterOption { Value = "none", Label = "Plain LDAP (389)" },
                    new SensorParameterOption { Value = "starttls", Label = "StartTLS (389)" },
                    new SensorParameterOption { Value = "ldaps", Label = "LDAPS (636)" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "ldap.baseDn",
                Label = "Search base (optional)",
                Kind = SensorParameterKind.Text,
                Description = "Search under this DN and count the entries. Empty = no search, only the root DSE and the bind.",
                Placeholder = "OU=Users,DC=corp,DC=example"
            },
            new SensorParameterDefinition
            {
                Key = "ldap.filter",
                Label = "Search filter",
                Kind = SensorParameterKind.Text,
                Description = "An LDAP filter (RFC 4515), e.g. (&(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))) for enabled AD users.",
                DefaultValue = "(objectClass=*)",
                VisibleWhenParameterKey = "ldap.baseDn"
            },
            new SensorParameterDefinition
            {
                Key = "ldap.scope",
                Label = "Search scope",
                Group = "Connection",
                Kind = SensorParameterKind.ValueList,
                DefaultValue = "sub",
                Options =
                [
                    new SensorParameterOption { Value = "base", Label = "The base entry only" },
                    new SensorParameterOption { Value = "one", Label = "One level below" },
                    new SensorParameterOption { Value = "sub", Label = "The whole subtree" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "ldap.port",
                Label = "Port",
                Group = "Connection",
                Kind = SensorParameterKind.Integer,
                Description = "Empty = 389, or 636 with LDAPS.",
                Placeholder = "389",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "ldap.verifySsl",
                Label = "Verify SSL",
                Group = "Connection",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the server's certificate (a domain controller's usually comes from an internal CA the probe does not know).",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var ldaps = context.OpenTcpPorts.Contains(636);
        if (!ldaps && !context.OpenTcpPorts.Contains(389))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["ldap.security"] = ldaps ? "ldaps" : "none";
        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(
            new SensorDiscoverySuggestion(Definition.Key, "Directory (LDAP)", context.Host, settings,
                $"Port {(ldaps ? 636 : 389)} is open - a directory server (Active Directory, OpenLDAP …) answers here.", 80)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var host = context.Target?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The directory server's address is missing (the host's address, or a target).");
        }

        var settings = context.Settings;
        var security = MonitoringSettings.TryReadParameter(settings, "ldap.security", out var configuredSecurity) ? configuredSecurity.Trim().ToLowerInvariant() : "none";
        var port = MonitoringSettings.TryReadParameterInt(settings, "ldap.port", out var configuredPort) && configuredPort is >= 1 and <= 65535 ? configuredPort : security == "ldaps" ? 636 : 389;
        MonitoringSettings.TryReadParameter(settings, "generic.username", out var username);
        MonitoringSettings.TryReadParameter(settings, "generic.password", out var password);
        MonitoringSettings.TryReadParameter(settings, "ldap.baseDn", out var baseDn);
        var filter = MonitoringSettings.TryReadParameter(settings, "ldap.filter", out var configuredFilter) && !string.IsNullOrWhiteSpace(configuredFilter) ? configuredFilter.Trim() : "(objectClass=*)";
        var scope = (MonitoringSettings.TryReadParameter(settings, "ldap.scope", out var configuredScope) ? configuredScope.Trim().ToLowerInvariant() : "sub") switch { "base" => 0, "one" => 1, _ => 2 };
        var verifySsl = MonitoringSettings.TryReadParameterBool(settings, "ldap.verifySsl", out var verify) && verify;
        var timeout = settings.Timeout ?? TimeSpan.FromSeconds(10);

        byte[]? encodedFilter = null;
        if (!string.IsNullOrWhiteSpace(baseDn))
        {
            try
            {
                encodedFilter = LdapFilter.Encode(filter);
            }
            catch (FormatException exception)
            {
                return SensorExecutionResult.Critical(TimeSpan.Zero, $"The search filter is not valid: {exception.Message}");
            }
        }

        if (!string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "A bind user needs a password (an empty password is an anonymous bind on most servers, which is not what it looks like).");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var label = $"{host}:{port}";
        var watch = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, timeoutCts.Token);
            Stream stream = tcp.GetStream();
            var session = new LdapSession(stream);
            if (security == "ldaps")
            {
                stream = await SecureAsync(stream, host, verifySsl, timeoutCts.Token);
                session.Upgrade(stream);
            }
            else if (security == "starttls")
            {
                var started = await session.ExtendedAsync("1.3.6.1.4.1.1466.20037", timeoutCts.Token);
                if (started.Code != 0)
                {
                    return SensorExecutionResult.Critical(watch.Elapsed, $"{label} refused StartTLS: {Describe(started)}");
                }

                stream = await SecureAsync(stream, host, verifySsl, timeoutCts.Token);
                session.Upgrade(stream);
            }

            var connectMs = watch.Elapsed.TotalMilliseconds;
            var rootDse = await session.SearchAsync(string.Empty, 0, LdapFilter.Encode("(objectClass=*)"), RootDseAttributes, 1, timeoutCts.Token);

            double? bindMs = null;
            if (!string.IsNullOrEmpty(username))
            {
                var started = watch.Elapsed;
                var bind = await session.BindAsync(username.Trim(), password!, timeoutCts.Token);
                bindMs = (watch.Elapsed - started).TotalMilliseconds;
                if (bind.Code != 0)
                {
                    return SensorExecutionResult.Critical(watch.Elapsed, $"{label} refused the bind as {username.Trim()}: {Describe(bind)}");
                }
            }

            int? entries = null;
            var truncated = false;
            if (encodedFilter is not null)
            {
                var search = await session.SearchAsync(baseDn!.Trim(), scope, encodedFilter, ["1.1"], SearchSizeLimit, timeoutCts.Token);
                if (search.Done.Code is not (0 or 4))
                {
                    return SensorExecutionResult.Critical(watch.Elapsed, $"The search under '{baseDn.Trim()}' failed: {Describe(search.Done)}");
                }

                entries = search.Entries.Count;
                truncated = search.Done.Code == 4;
            }

            watch.Stop();
            await session.UnbindAsync();
            var reading = new LdapReading(label, connectMs, bindMs, watch.Elapsed.TotalMilliseconds, entries, truncated,
                rootDse.Entries.FirstOrDefault()?.Attributes ?? new Dictionary<string, IReadOnlyList<string>>(), username?.Trim(), baseDn?.Trim(), security);
            return SensorThresholdEvaluator.ApplyChannelThresholds(settings, BuildResult(reading, DateTimeOffset.UtcNow, watch.Elapsed));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{label} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (SocketException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"Nothing listens on {label}.",
                SocketError.HostNotFound or SocketError.NoData => $"The name '{host}' does not resolve.",
                _ => $"{label} cannot be reached: {exception.Message}"
            });
        }
        catch (System.Security.Authentication.AuthenticationException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The TLS handshake with {label} failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or FormatException)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{label} does not answer like an LDAP server ({exception.Message}).");
        }
    }

    // ---- the result (pure) -----------------------------------------------------------------------------------------------

    public sealed record LdapReading(
        string Label,
        double ConnectMs,
        double? BindMs,
        double TotalMs,
        int? Entries,
        bool Truncated,
        IReadOnlyDictionary<string, IReadOnlyList<string>> RootDse,
        string? BindUser,
        string? BaseDn,
        string Security);

    public static SensorExecutionResult BuildResult(LdapReading reading, DateTimeOffset now, TimeSpan duration)
    {
        string? Attribute(string name) => reading.RootDse.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
        var isAd = Attribute("domainControllerFunctionality") is not null || Attribute("isSynchronized") is not null;

        var channels = new List<SensorChannelValue>
        {
            new() { Key = ResponseTimeChannel, Label = "Response time", Value = Math.Round(reading.TotalMs, 1), Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration, IsDefault = true },
            new() { Key = "connectTime", Label = "Connect time", Value = Math.Round(reading.ConnectMs, 1), Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration, LogByDefault = false }
        };
        if (reading.BindMs is { } bind)
        {
            channels.Add(new SensorChannelValue { Key = "bindTime", Label = "Bind time", Value = Math.Round(bind, 1), Unit = "ms", MeasurementKind = SensorMeasurementKind.Duration, LogByDefault = false });
        }

        if (reading.Entries is { } entries)
        {
            channels.Add(new SensorChannelValue { Key = "entries", Label = "Entries found", Value = entries, MeasurementKind = SensorMeasurementKind.Count });
        }

        double? skew = null;
        if (Attribute("currentTime") is { } currentTime && ParseGeneralizedTime(currentTime) is { } serverTime)
        {
            skew = Math.Round(Math.Abs((serverTime - now).TotalSeconds), 1);
            channels.Add(new SensorChannelValue { Key = ClockSkewChannel, Label = "Clock skew", Value = skew, Unit = "s" });
        }

        bool? synchronized = Attribute("isSynchronized") is { } sync ? sync.Equals("TRUE", StringComparison.OrdinalIgnoreCase) : null;
        if (synchronized is { } isSynced)
        {
            channels.Add(new SensorChannelValue { Key = "synchronized", Label = "Synchronized", Value = isSynced ? 1 : 0, MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false });
        }

        var builder = new StringBuilder(Attribute("dnsHostName") ?? reading.Label);
        builder.Append(reading.Security switch { "ldaps" => " (LDAPS)", "starttls" => " (StartTLS)", _ => string.Empty });
        builder.Append(": ").Append(reading.BindUser is { Length: > 0 } user ? $"bind as {user} OK" : "answers (anonymous)");
        if (reading.Entries is { } count)
        {
            builder.Append($", {count}{(reading.Truncated ? "+" : string.Empty)} entr{(count == 1 && !reading.Truncated ? "y" : "ies")} under {reading.BaseDn}");
        }

        var details = new List<string>();
        if (isAd)
        {
            details.Add("Active Directory");
        }
        else if (Attribute("vendorName") is { } vendor)
        {
            details.Add(vendor.Trim() + (Attribute("vendorVersion") is { } version ? " " + version.Trim() : string.Empty));
        }

        if (skew is { } seconds)
        {
            details.Add($"clock {seconds.ToString("0.#", CultureInfo.InvariantCulture)} s off");
        }

        if (synchronized == false)
        {
            details.Add("NOT synchronized");
        }

        if (details.Count > 0)
        {
            builder.Append(" (").Append(string.Join(", ", details)).Append(')');
        }

        var message = builder.ToString();
        var value = Math.Round(reading.TotalMs, 1);
        return synchronized == false
            ? SensorExecutionResult.Warning(duration, message, value, ResponseTimeChannel, channels)
            : SensorExecutionResult.Healthy(duration, message, value, ResponseTimeChannel, channels);
    }

    /// <summary>An LDAP result in words - with Active Directory's own reason behind a refused bind ("data 52e" …).</summary>
    public static string Describe(LdapResult result)
    {
        var name = result.Code switch
        {
            1 => "operations error",
            2 => "protocol error",
            3 => "time limit exceeded",
            7 => "authentication method not supported",
            8 or 13 => "the server wants an encrypted connection - use LDAPS or StartTLS",
            32 => "no such object (the DN does not exist)",
            34 => "invalid DN syntax",
            48 => "inappropriate authentication",
            49 => "invalid credentials",
            50 => "insufficient access rights",
            51 => "server busy",
            52 => "server unavailable",
            53 => "unwilling to perform",
            _ => "result " + result.Code.ToString(CultureInfo.InvariantCulture)
        };
        var adReason = Regex.Match(result.Message ?? string.Empty, @"data ([0-9a-fA-F]+)") is { Success: true } match
            ? match.Groups[1].Value.ToLowerInvariant() switch
            {
                "525" => "user not found",
                "52e" => "wrong password",
                "530" => "not allowed to log on at this time",
                "531" => "not allowed to log on from this workstation",
                "532" => "password expired",
                "533" => "account disabled",
                "701" => "account expired",
                "773" => "the user must change the password",
                "775" => "account locked out",
                _ => null
            }
            : null;
        return adReason is not null ? $"{name} - {adReason}" : string.IsNullOrWhiteSpace(result.Message) ? name : $"{name} ({result.Message.Trim()})";
    }

    /// <summary>GeneralizedTime as AD writes it: 20261023194500.0Z.</summary>
    public static DateTimeOffset? ParseGeneralizedTime(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(\d{14})(\.\d+)?Z$");
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    private static async Task<Stream> SecureAsync(Stream inner, string host, bool verify, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            RemoteCertificateValidationCallback = verify ? null : (_, _, _, _) => true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken);
        return ssl;
    }

    // ---- LDAPv3 on the wire (RFC 4511) -----------------------------------------------------------------------------------

    public sealed record LdapResult(int Code, string? Message);

    public sealed record LdapEntry(string Dn, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes);

    public sealed record LdapSearchResult(IReadOnlyList<LdapEntry> Entries, LdapResult Done);

    private sealed class LdapSession(Stream stream)
    {
        private int _messageId;
        private Stream _stream = stream;

        /// <summary>After LDAPS / StartTLS: the same session (message ids go on) over the encrypted stream.</summary>
        public void Upgrade(Stream secured) => _stream = secured;

        public async Task<LdapResult> BindAsync(string name, string password, CancellationToken cancellationToken)
        {
            var id = await SendAsync(0x60, Concat(Int(0x02, 3), Octets(0x04, name), Octets(0x80, password)), cancellationToken);
            return ReadResult(await ReceiveAsync(id, cancellationToken), 0x61);
        }

        public async Task<LdapResult> ExtendedAsync(string oid, CancellationToken cancellationToken)
        {
            var id = await SendAsync(0x77, Octets(0x80, oid), cancellationToken);
            return ReadResult(await ReceiveAsync(id, cancellationToken), 0x78);
        }

        public async Task<LdapSearchResult> SearchAsync(string baseDn, int scope, byte[] filter, IReadOnlyList<string> attributes, int sizeLimit, CancellationToken cancellationToken)
        {
            var request = Concat(
                Octets(0x04, baseDn),
                Int(0x0A, scope),
                Int(0x0A, 0),
                Int(0x02, sizeLimit),
                Int(0x02, 0),
                SnmpSensorExecutor.EncodeTagged(0x01, [0x00]),
                filter,
                SnmpSensorExecutor.EncodeSequence(Concat(attributes.Select(attribute => Octets(0x04, attribute)).ToArray())));
            var id = await SendAsync(0x63, request, cancellationToken);
            var entries = new List<LdapEntry>();
            while (true)
            {
                var operation = await ReceiveAsync(id, cancellationToken);
                if (operation.Tag == 0x64)
                {
                    entries.Add(ReadEntry(operation));
                }
                else if (operation.Tag == 0x65)
                {
                    return new LdapSearchResult(entries, ReadResult(operation, 0x65));
                }

                // 0x73 = a search result reference (a referral elsewhere) - not an entry of this server
            }
        }

        public async Task UnbindAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync(0x42, [], deadline.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // the connection closes either way
            }
        }

        private async Task<int> SendAsync(byte operationTag, byte[] operation, CancellationToken cancellationToken)
        {
            var id = ++_messageId;
            var message = SnmpSensorExecutor.EncodeSequence(Concat(Int(0x02, id), SnmpSensorExecutor.EncodeTagged(operationTag, operation)));
            await _stream.WriteAsync(message, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            return id;
        }

        /// <summary>The next protocol operation for this message id (another id - an unsolicited notice - is skipped).</summary>
        private async Task<BerElement> ReceiveAsync(int id, CancellationToken cancellationToken)
        {
            while (true)
            {
                var message = await ReadMessageAsync(cancellationToken);
                var reader = new SnmpSensorExecutor.BerReader(message);
                var messageId = reader.ReadElement();
                var operation = reader.ReadElement();
                if (messageId.Tag == 0x02 && SnmpSensorExecutor.DecodeSignedInteger(messageId.Content) == id)
                {
                    return operation;
                }

                if (operation.Tag == 0x78)
                {
                    // Notice of disconnection (message id 0): the server is closing the connection.
                    throw new IOException("the server closed the connection: " + Describe(ReadResult(operation, 0x78)));
                }
            }
        }

        private async Task<byte[]> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var tag = await ReadByteAsync(cancellationToken);
            if (tag != 0x30)
            {
                throw new FormatException($"tag 0x{tag:X2} where a message should start");
            }

            var first = await ReadByteAsync(cancellationToken);
            var length = first;
            if ((first & 0x80) != 0)
            {
                var count = first & 0x7F;
                if (count is 0 or > 4)
                {
                    throw new FormatException("a length it cannot read");
                }

                length = 0;
                for (var index = 0; index < count; index++)
                {
                    length = (length << 8) | await ReadByteAsync(cancellationToken);
                }
            }

            if (length > 16 * 1024 * 1024)
            {
                throw new FormatException("a message larger than 16 MB");
            }

            var content = new byte[length];
            await _stream.ReadExactlyAsync(content, cancellationToken);
            return content;
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            var one = new byte[1];
            await _stream.ReadExactlyAsync(one, cancellationToken);
            return one[0];
        }
    }

    private static LdapResult ReadResult(BerElement operation, byte expectedTag)
    {
        if (operation.Tag != expectedTag)
        {
            throw new FormatException($"operation 0x{operation.Tag:X2} where 0x{expectedTag:X2} was expected");
        }

        var reader = new SnmpSensorExecutor.BerReader(operation.Content);
        var code = (int)SnmpSensorExecutor.DecodeSignedInteger(reader.ReadElement().Content);
        reader.ReadElement(); // matchedDN
        var message = reader.TryReadElement(out var diagnostic) ? Encoding.UTF8.GetString(diagnostic.Content) : null;
        return new LdapResult(code, message);
    }

    private static LdapEntry ReadEntry(BerElement operation)
    {
        var reader = new SnmpSensorExecutor.BerReader(operation.Content);
        var dn = Encoding.UTF8.GetString(reader.ReadElement().Content);
        var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (reader.TryReadElement(out var list))
        {
            var attributeReader = new SnmpSensorExecutor.BerReader(list.Content);
            while (attributeReader.TryReadElement(out var attribute))
            {
                var parts = new SnmpSensorExecutor.BerReader(attribute.Content);
                var type = Encoding.UTF8.GetString(parts.ReadElement().Content);
                var values = new List<string>();
                if (parts.TryReadElement(out var set))
                {
                    var valueReader = new SnmpSensorExecutor.BerReader(set.Content);
                    while (valueReader.TryReadElement(out var value))
                    {
                        values.Add(Encoding.UTF8.GetString(value.Content));
                    }
                }

                attributes[type] = values;
            }
        }

        return new LdapEntry(dn, attributes);
    }

    private static byte[] Int(byte tag, long value)
    {
        // EncodeInteger gives a whole INTEGER TLV; ENUMERATED is the same content under its own tag.
        var encoded = SnmpSensorExecutor.EncodeInteger(value);
        return tag == 0x02 ? encoded : SnmpSensorExecutor.EncodeTagged(tag, encoded[2..]);
    }

    private static byte[] Octets(byte tag, string text) => SnmpSensorExecutor.EncodeTagged(tag, Encoding.UTF8.GetBytes(text));

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    // ---- filters (RFC 4515) ----------------------------------------------------------------------------------------------

    /// <summary>Parses a string filter into its BER form: (&amp;…) (|…) (!…), =, =*, substrings with *, &gt;=, &lt;=, ~=, and
    /// extensible matches (attr:rule:=value, as AD's bit-mask rules need). \XX escapes a byte.</summary>
    public static class LdapFilter
    {
        public static byte[] Encode(string filter)
        {
            var text = filter.Trim();
            if (!text.StartsWith('('))
            {
                text = "(" + text + ")";
            }

            var position = 0;
            var encoded = Parse(text, ref position);
            if (position != text.Length)
            {
                throw new FormatException($"unexpected '{text[position..]}' after the filter");
            }

            return encoded;
        }

        private static byte[] Parse(string text, ref int position)
        {
            Expect(text, ref position, '(');
            if (position >= text.Length)
            {
                throw new FormatException("the filter ends too early");
            }

            byte[] result;
            switch (text[position])
            {
                case '&':
                case '|':
                {
                    var tag = text[position] == '&' ? (byte)0xA0 : (byte)0xA1;
                    position++;
                    var parts = new List<byte[]>();
                    while (position < text.Length && text[position] == '(')
                    {
                        parts.Add(Parse(text, ref position));
                    }

                    if (parts.Count == 0)
                    {
                        throw new FormatException("an empty & or |");
                    }

                    result = SnmpSensorExecutor.EncodeTagged(tag, Concat(parts.ToArray()));
                    break;
                }

                case '!':
                    position++;
                    result = SnmpSensorExecutor.EncodeTagged(0xA2, Parse(text, ref position));
                    break;
                default:
                    result = ParseItem(text, ref position);
                    break;
            }

            Expect(text, ref position, ')');
            return result;
        }

        private static byte[] ParseItem(string text, ref int position)
        {
            var end = FindClosing(text, position);
            var item = text[position..end];
            position = end;

            var extensible = Regex.Match(item, @"^(?<attr>[^:=]*)(?<dn>:dn)?(:(?<rule>[^:=]+))?:=(?<value>.*)$");
            if (item.Contains(":=") && extensible.Success)
            {
                var parts = new List<byte[]>();
                if (extensible.Groups["rule"].Success)
                {
                    parts.Add(Octets(0x81, extensible.Groups["rule"].Value));
                }

                if (extensible.Groups["attr"].Value.Length > 0)
                {
                    parts.Add(Octets(0x82, extensible.Groups["attr"].Value));
                }

                parts.Add(SnmpSensorExecutor.EncodeTagged(0x83, Unescape(extensible.Groups["value"].Value)));
                if (extensible.Groups["dn"].Success)
                {
                    parts.Add(SnmpSensorExecutor.EncodeTagged(0x84, [0xFF]));
                }

                return SnmpSensorExecutor.EncodeTagged(0xA9, Concat(parts.ToArray()));
            }

            foreach (var (op, tag) in new[] { (">=", (byte)0xA5), ("<=", (byte)0xA6), ("~=", (byte)0xA8) })
            {
                var index = item.IndexOf(op, StringComparison.Ordinal);
                if (index > 0)
                {
                    return SnmpSensorExecutor.EncodeTagged(tag, Concat(Octets(0x04, Attribute(item[..index])), SnmpSensorExecutor.EncodeTagged(0x04, Unescape(item[(index + 2)..]))));
                }
            }

            var equals = item.IndexOf('=');
            if (equals <= 0)
            {
                throw new FormatException($"'{item}' is not attribute=value");
            }

            var attribute = Attribute(item[..equals]);
            var value = item[(equals + 1)..];
            if (value == "*")
            {
                return SnmpSensorExecutor.EncodeTagged(0x87, Encoding.UTF8.GetBytes(attribute));
            }

            if (!value.Contains('*'))
            {
                return SnmpSensorExecutor.EncodeTagged(0xA3, Concat(Octets(0x04, attribute), SnmpSensorExecutor.EncodeTagged(0x04, Unescape(value))));
            }

            var pieces = value.Split('*');
            var substrings = new List<byte[]>();
            for (var index = 0; index < pieces.Length; index++)
            {
                if (pieces[index].Length == 0)
                {
                    continue;
                }

                var tag = index == 0 ? (byte)0x80 : index == pieces.Length - 1 ? (byte)0x82 : (byte)0x81;
                substrings.Add(SnmpSensorExecutor.EncodeTagged(tag, Unescape(pieces[index])));
            }

            return SnmpSensorExecutor.EncodeTagged(0xA4, Concat(Octets(0x04, attribute), SnmpSensorExecutor.EncodeSequence(Concat(substrings.ToArray()))));
        }

        private static string Attribute(string text)
        {
            var attribute = text.Trim();
            if (attribute.Length == 0 || !Regex.IsMatch(attribute, @"^[A-Za-z0-9][A-Za-z0-9.;-]*$"))
            {
                throw new FormatException($"'{text}' is not an attribute name");
            }

            return attribute;
        }

        private static byte[] Unescape(string value)
        {
            var bytes = new List<byte>();
            var plain = new StringBuilder();
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '\\')
                {
                    plain.Append(value[index]);
                    continue;
                }

                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var escaped))
                {
                    throw new FormatException("a \\ without two hex digits");
                }

                bytes.AddRange(Encoding.UTF8.GetBytes(plain.ToString()));
                plain.Clear();
                bytes.Add(escaped);
                index += 2;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(plain.ToString()));
            return bytes.ToArray();
        }

        private static int FindClosing(string text, int position)
        {
            for (var index = position; index < text.Length; index++)
            {
                if (text[index] == '(')
                {
                    throw new FormatException("a '(' inside a comparison - escape it as \\28");
                }

                if (text[index] == ')')
                {
                    return index;
                }
            }

            throw new FormatException("a missing ')'");
        }

        private static void Expect(string text, ref int position, char expected)
        {
            if (position >= text.Length || text[position] != expected)
            {
                throw new FormatException($"'{expected}' expected at position {position + 1}");
            }

            position++;
        }
    }
}
