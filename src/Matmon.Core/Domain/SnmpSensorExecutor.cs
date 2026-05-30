using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

public sealed class SnmpSensorExecutor : ISensorExecutor
{
    private const string DefaultVersion = "v2c";
    private const int DefaultPort = 161;
    private const int DefaultWalkLimit = 250;

    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "snmp",
        DisplayName = "SNMP",
        Description = "Poll numeric OIDs over SNMP v1/v2c/v3. Use Walk to discover OIDs and pick the ones you want.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "snmp.community",
                Label = "Community",
                Kind = SensorParameterKind.Text,
                Description = "SNMP community string for v1/v2c",
                DefaultValue = "public",
                CredentialKind = MonitoringCredentialKind.Snmp
            },
            new SensorParameterDefinition
            {
                Key = "snmp.port",
                Label = "Port",
                Kind = SensorParameterKind.Integer,
                Description = "SNMP UDP port",
                DefaultValue = "161",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "snmp.version",
                Label = "Version",
                Kind = SensorParameterKind.ValueList,
                Description = "SNMP protocol version.",
                DefaultValue = "v2c",
                Options =
                [
                    new SensorParameterOption { Value = "v1", Label = "v1" },
                    new SensorParameterOption { Value = "v2c", Label = "v2c" },
                    new SensorParameterOption { Value = "v3", Label = "v3" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.v3.username",
                Label = "SNMPv3 username",
                Kind = SensorParameterKind.Text,
                Description = "Required for SNMPv3",
                Placeholder = "snmp-user",
                CredentialKind = MonitoringCredentialKind.Snmp,
                VisibleWhenParameterKey = "snmp.version",
                VisibleWhenValues = ["v3"]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.v3.authProtocol",
                Label = "SNMPv3 auth protocol",
                Kind = SensorParameterKind.ValueList,
                Description = "Authentication protocol for SNMPv3",
                DefaultValue = "sha1",
                VisibleWhenParameterKey = "snmp.version",
                VisibleWhenValues = ["v3"],
                Options =
                [
                    new SensorParameterOption { Value = "none", Label = "none" },
                    new SensorParameterOption { Value = "md5", Label = "md5" },
                    new SensorParameterOption { Value = "sha1", Label = "sha1" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.v3.authPassword",
                Label = "SNMPv3 auth password",
                Kind = SensorParameterKind.Secret,
                Description = "Authentication password for SNMPv3",
                CredentialKind = MonitoringCredentialKind.Snmp,
                VisibleWhenParameterKey = "snmp.version",
                VisibleWhenValues = ["v3"]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.v3.privProtocol",
                Label = "SNMPv3 privacy protocol",
                Kind = SensorParameterKind.ValueList,
                Description = "Privacy protocol for SNMPv3",
                DefaultValue = "none",
                VisibleWhenParameterKey = "snmp.version",
                VisibleWhenValues = ["v3"],
                Options =
                [
                    new SensorParameterOption { Value = "none", Label = "none" },
                    new SensorParameterOption { Value = "des", Label = "des" },
                    new SensorParameterOption { Value = "aes128", Label = "aes128" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.v3.privPassword",
                Label = "SNMPv3 privacy password",
                Kind = SensorParameterKind.Secret,
                Description = "Privacy password for SNMPv3",
                CredentialKind = MonitoringCredentialKind.Snmp,
                VisibleWhenParameterKey = "snmp.version",
                VisibleWhenValues = ["v3"]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.v3.contextName",
                Label = "SNMPv3 context",
                Kind = SensorParameterKind.Text,
                Description = "Optional SNMPv3 context name",
                VisibleWhenParameterKey = "snmp.version",
                VisibleWhenValues = ["v3"]
            },
            new SensorParameterDefinition
            {
                Key = "snmp.oids",
                Label = "Selected OIDs",
                Kind = SensorParameterKind.Multiline,
                Description = "One OID per line. The Walk button can fill this field.",
                Placeholder = """
1.3.6.1.2.1.1.3.0
1.3.6.1.2.1.1.5.0
"""
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.SnmpResponded)
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["snmp.community"] = string.IsNullOrWhiteSpace(context.SnmpCommunity)
            ? "public"
            : context.SnmpCommunity.Trim();
        settings.Parameters["snmp.version"] = string.IsNullOrWhiteSpace(context.SnmpVersion)
            ? DefaultVersion
            : context.SnmpVersion.Trim();
        settings.Parameters["snmp.port"] = (context.SnmpPort is >= 1 and <= 65535 ? context.SnmpPort : DefaultPort)
            .ToString(CultureInfo.InvariantCulture);
        settings.Parameters["snmp.oids"] = "1.3.6.1.2.1.1.3.0|Uptime";

        var reason = string.IsNullOrWhiteSpace(context.SnmpSummary)
            ? "SNMP answered."
            : $"SNMP answered: {context.SnmpSummary}";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "SNMP Uptime",
                    string.Empty,
                    settings,
                    reason,
                    90)));
    }

    public static ValueTask<IReadOnlyList<SnmpDiscoveryItem>> DiscoverAsync(
        string target,
        string community,
        int port,
        string version,
        string rootOid,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var settings = new MonitoringSettings();
        settings.Parameters["snmp.community"] = community;
        settings.Parameters["snmp.port"] = port.ToString(CultureInfo.InvariantCulture);
        settings.Parameters["snmp.version"] = version;
        return DiscoverAsync(target, settings, rootOid, timeout, cancellationToken);
    }

    public static ValueTask<IReadOnlyList<SnmpDiscoveryItem>> DiscoverAsync(
        string target,
        MonitoringSettings settings,
        string rootOid,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var normalizedVersion = ResolveVersion(settings);
        if (normalizedVersion == "v3")
        {
            return ValueTask.FromResult<IReadOnlyList<SnmpDiscoveryItem>>(
                DiscoverV3(target, settings, rootOid, timeout, cancellationToken));
        }

        var community = ResolveCommunity(settings);
        var port = ResolvePort(settings);

        target = target?.Trim() ?? string.Empty;

        var normalizedRootOid = NormalizeOid(rootOid);
        if (string.IsNullOrWhiteSpace(normalizedRootOid))
        {
            normalizedRootOid = "1.3.6.1.2.1";
        }

        var items = new List<SnmpDiscoveryItem>();
        var previousOid = normalizedRootOid;

        for (var index = 0; index < DefaultWalkLimit; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = SendRequest(
                target,
                port,
                community,
                normalizedVersion,
                SnmpPduType.GetNextRequest,
                [previousOid],
                timeout,
                cancellationToken);

            if (response.ErrorStatus != 0)
            {
                break;
            }

            if (response.VarBinds.Count == 0)
            {
                break;
            }

            var varBind = response.VarBinds[0];
            if (!IsUnderRoot(varBind.Oid, normalizedRootOid) ||
                string.Equals(varBind.Oid, previousOid, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            items.Add(new SnmpDiscoveryItem(
                varBind.Oid,
                varBind.Value.Syntax,
                varBind.Value.DisplayValue,
                varBind.Value.NumericValue.HasValue,
                varBind.Value.NumericValue.HasValue));

            previousOid = varBind.Oid;

            if (varBind.Value.IsEndOfMib)
            {
                break;
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SnmpDiscoveryItem>>(items);
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var target = context.Target?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "target is required"));
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "snmp.oids", out var rawOids) ||
            string.IsNullOrWhiteSpace(rawOids))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "at least one OID is required"));
        }

        var version = ResolveVersion(context.Settings);
        if (version == "v3")
        {
            return ExecuteV3Async(context, cancellationToken);
        }

        var community = ResolveCommunity(context.Settings);
        var port = ResolvePort(context.Settings);
        var selections = ParseOidSelections(rawOids).ToArray();
        if (selections.Length == 0)
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "at least one OID is required"));
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();

        try
        {
            var response = SendRequest(
                target,
                port,
                community,
                version,
                SnmpPduType.GetRequest,
                selections.Select(selection => selection.Oid).ToArray(),
                timeout,
                cancellationToken);

            if (response.ErrorStatus != 0)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        BuildErrorMessage(response.ErrorStatus, response.ErrorIndex)));
            }

            if (response.VarBinds.Count == 0)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(watch.Elapsed, "SNMP returned no values"));
            }

            if (response.VarBinds.Count != selections.Length)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        "SNMP response did not contain all requested OIDs"));
            }

            var channels = BuildChannels(selections, response.VarBinds);
            if (channels.Count == 0)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(watch.Elapsed, "SNMP returned no values"));
            }

            var defaultChannel = SelectDefaultChannel(channels);
            if (defaultChannel is null)
            {
                var firstChannel = channels[0];
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        "SNMP returned no numeric values",
                        null,
                        firstChannel.Key,
                        MarkDefault(channels, firstChannel.Key)));
            }

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                BuildMessage(defaultChannel, channels.Count),
                defaultChannel.Value,
                defaultChannel.Key,
                MarkDefault(channels, defaultChannel.Key));

            return ValueTask.FromResult(SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Unknown("execution cancelled"));
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, $"execution timed out after {timeout.TotalSeconds:0.#} seconds"));
        }
        catch (SocketException ex)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, BuildConnectionFailureMessage(target, port, ex)));
        }
        catch (Exception ex)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, ex.Message));
        }
    }

    private static List<SensorChannelValue> BuildChannels(
        IReadOnlyList<SnmpOidSelection> selections,
        IReadOnlyList<SnmpVarBind> varBinds)
    {
        var channels = new List<SensorChannelValue>(Math.Min(selections.Count, varBinds.Count));
        var count = Math.Min(selections.Count, varBinds.Count);

        for (var index = 0; index < count; index++)
        {
            var selection = selections[index];
            var varBind = varBinds[index];
            var label = string.IsNullOrWhiteSpace(selection.Label) ? selection.Oid : selection.Label.Trim();

            channels.Add(new SensorChannelValue
            {
                Key = NormalizeKey(label),
                Label = label,
                Value = varBind.Value.NumericValue,
                Unit = varBind.Value.Unit,
                Message = varBind.Value.NumericValue.HasValue ? null : varBind.Value.DisplayValue
            });
        }

        return channels;
    }

    private static SensorChannelValue? SelectDefaultChannel(IReadOnlyList<SensorChannelValue> channels)
    {
        return channels.FirstOrDefault(channel => channel.Value.HasValue);
    }

    private static List<SensorChannelValue> MarkDefault(
        IReadOnlyList<SensorChannelValue> channels,
        string defaultChannelKey)
    {
        return channels
            .Select(channel => channel with
            {
                IsDefault = string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private static string BuildMessage(SensorChannelValue defaultChannel, int channelCount)
    {
        var label = string.IsNullOrWhiteSpace(defaultChannel.Label) ? defaultChannel.Key : defaultChannel.Label;
        if (!defaultChannel.Value.HasValue)
        {
            return defaultChannel.Message ?? $"{label} returned no numeric value";
        }

        return string.IsNullOrWhiteSpace(defaultChannel.Unit)
            ? $"{label} {FormatNumber(defaultChannel.Value.Value)}"
            : $"{label} {FormatNumber(defaultChannel.Value.Value)} {defaultChannel.Unit}";
    }

    private static string BuildErrorMessage(int errorStatus, int errorIndex)
    {
        return errorStatus switch
        {
            1 => "SNMP request too big",
            2 => "SNMP no such name",
            3 => "SNMP bad value",
            4 => "SNMP read only",
            5 => "SNMP general error",
            _ => errorIndex > 0
                ? $"SNMP error status {errorStatus} at index {errorIndex}"
                : $"SNMP error status {errorStatus}"
        };
    }

    private static SnmpResponse SendRequest(
        string target,
        int port,
        string community,
        string version,
        SnmpPduType pduType,
        IReadOnlyList<string> oids,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var packet = BuildMessage(version, community, pduType, requestId, oids);
        var timeoutMs = Math.Max(1000, (int)Math.Ceiling(timeout.TotalMilliseconds));

        using var client = new UdpClient();
        client.Client.SendTimeout = timeoutMs;
        client.Client.ReceiveTimeout = timeoutMs;
        client.Connect(target, port);
        cancellationToken.ThrowIfCancellationRequested();
        client.Send(packet, packet.Length);

        var remote = new IPEndPoint(IPAddress.Any, 0);
        var response = client.Receive(ref remote);
        return ParseResponse(response, requestId);
    }

    private static byte[] BuildMessage(
        string version,
        string community,
        SnmpPduType pduType,
        int requestId,
        IReadOnlyList<string> oids)
    {
        var body = new List<byte>();
        body.AddRange(EncodeInteger(ParseVersionCode(version)));
        body.AddRange(EncodeOctetString(Encoding.ASCII.GetBytes(community)));
        body.AddRange(BuildPdu(pduType, requestId, oids));
        return EncodeSequence(body.ToArray());
    }

    private static byte[] BuildPdu(
        SnmpPduType pduType,
        int requestId,
        IReadOnlyList<string> oids)
    {
        var content = new List<byte>();
        content.AddRange(EncodeInteger(requestId));
        content.AddRange(EncodeInteger(0));
        content.AddRange(EncodeInteger(0));
        content.AddRange(EncodeSequence(BuildVarBindList(oids)));
        return EncodeTagged((byte)(0xA0 + (byte)pduType), content.ToArray());
    }

    private static byte[] BuildVarBindList(IReadOnlyList<string> oids)
    {
        var content = new List<byte>();
        foreach (var oid in oids)
        {
            var varBind = new List<byte>();
            varBind.AddRange(EncodeObjectIdentifier(NormalizeOid(oid)));
            varBind.AddRange(EncodeNull());
            content.AddRange(EncodeSequence(varBind.ToArray()));
        }

        return content.ToArray();
    }

    private static SnmpResponse ParseResponse(byte[] payload, int expectedRequestId)
    {
        var reader = new BerReader(payload);
        var messageElement = reader.ReadElement();
        EnsureTag(messageElement.Tag, 0x30, "SNMP message");

        var messageReader = new BerReader(messageElement.Content);
        _ = DecodeInteger(messageReader.ReadElement(), 0x02, "SNMP version");
        _ = messageReader.ReadElement();

        var pduElement = messageReader.ReadElement();
        return ParsePdu(pduElement, expectedRequestId);
    }

    private static SnmpResponse ParsePdu(BerElement pduElement, int expectedRequestId)
    {
        if (pduElement.Tag is not (0xA2 or 0xA8))
        {
            throw new InvalidOperationException($"Unexpected SNMP response type 0x{pduElement.Tag:X2}.");
        }

        var pduReader = new BerReader(pduElement.Content);
        var responseRequestId = (int)DecodeInteger(pduReader.ReadElement(), 0x02, "SNMP request id");
        if (responseRequestId != expectedRequestId)
        {
            throw new InvalidOperationException("SNMP response request id did not match the request.");
        }

        var errorStatus = (int)DecodeInteger(pduReader.ReadElement(), 0x02, "SNMP error status");
        var errorIndex = (int)DecodeInteger(pduReader.ReadElement(), 0x02, "SNMP error index");

        var varBindList = pduReader.ReadElement();
        EnsureTag(varBindList.Tag, 0x30, "SNMP varbind list");
        var varBindReader = new BerReader(varBindList.Content);
        var varBinds = new List<SnmpVarBind>();

        while (varBindReader.TryReadElement(out var varBindElement))
        {
            EnsureTag(varBindElement.Tag, 0x30, "SNMP varbind");
            var itemReader = new BerReader(varBindElement.Content);
            var oidElement = itemReader.ReadElement();
            EnsureTag(oidElement.Tag, 0x06, "SNMP varbind OID");
            var valueElement = itemReader.ReadElement();
            var oid = DecodeObjectIdentifier(oidElement.Content.AsSpan());
            var value = DecodeValue(valueElement.Tag, valueElement.Content.AsSpan());
            varBinds.Add(new SnmpVarBind(oid, value));
        }

        return new SnmpResponse(errorStatus, errorIndex, varBinds);
    }

    private static long DecodeInteger(BerElement element, byte expectedTag, string label)
    {
        EnsureTag(element.Tag, expectedTag, label);
        return DecodeSignedInteger(element.Content.AsSpan());
    }

    private static SnmpValue DecodeValue(byte tag, ReadOnlySpan<byte> content)
    {
        return tag switch
        {
            0x02 => new SnmpValue("Integer", FormatNumber(DecodeSignedInteger(content)), DecodeSignedInteger(content), null, false),
            0x04 => DecodeOctetStringValue(content),
            0x05 => new SnmpValue("Null", "null", null, null, false),
            0x06 => new SnmpValue("ObjectIdentifier", DecodeObjectIdentifier(content), null, null, false),
            0x40 => new SnmpValue(
                "IpAddress",
                content.Length is 4 or 16
                    ? new IPAddress(content.ToArray()).ToString()
                    : Convert.ToHexString(content),
                null,
                null,
                false),
            0x41 => new SnmpValue("Counter32", FormatNumber(DecodeUnsignedInteger(content)), DecodeUnsignedInteger(content), null, false),
            0x42 => new SnmpValue("Gauge32", FormatNumber(DecodeUnsignedInteger(content)), DecodeUnsignedInteger(content), null, false),
            0x43 => DecodeTimeTicksValue(content),
            0x46 => new SnmpValue("Counter64", FormatNumber(DecodeUnsignedInteger(content)), DecodeUnsignedInteger(content), null, false),
            0x80 => new SnmpValue("NoSuchObject", "noSuchObject", null, null, true),
            0x81 => new SnmpValue("NoSuchInstance", "noSuchInstance", null, null, true),
            0x82 => new SnmpValue("EndOfMibView", "endOfMibView", null, null, true),
            _ => new SnmpValue($"Tag 0x{tag:X2}", Convert.ToHexString(content), null, null, false)
        };
    }

    private static SnmpValue DecodeOctetStringValue(ReadOnlySpan<byte> content)
    {
        var text = DecodeOctetString(content);
        if (TryParseNumber(text, out var numericValue))
        {
            return new SnmpValue("OctetString", text, numericValue, null, false);
        }

        return new SnmpValue("OctetString", text, null, null, false);
    }

    private static SnmpValue DecodeTimeTicksValue(ReadOnlySpan<byte> content)
    {
        var ticks = DecodeUnsignedInteger(content);
        var seconds = ticks / 100.0;
        return new SnmpValue("TimeTicks", $"{FormatNumber(seconds)} s", seconds, "s", false);
    }

    private static long DecodeSignedInteger(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var value = new BigInteger(content, isUnsigned: false, isBigEndian: true);
        return (long)value;
    }

    private static double DecodeUnsignedInteger(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var value = new BigInteger(content, isUnsigned: true, isBigEndian: true);
        return (double)value;
    }

    private static string DecodeOctetString(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(content);
        return text.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character))
            ? Convert.ToHexString(content)
            : text.TrimEnd('\0');
    }

    private static string DecodeObjectIdentifier(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var values = new List<string>();
        var index = 0;
        ulong firstComponentValue = 0;
        while (index < content.Length)
        {
            var value = content[index];
            firstComponentValue = (firstComponentValue << 7) | (uint)(value & 0x7F);
            index++;
            if ((value & 0x80) != 0)
            {
                continue;
            }

            break;
        }

        if (firstComponentValue < 40)
        {
            values.Add("0");
            values.Add(firstComponentValue.ToString(CultureInfo.InvariantCulture));
        }
        else if (firstComponentValue < 80)
        {
            values.Add("1");
            values.Add((firstComponentValue - 40).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            values.Add("2");
            values.Add((firstComponentValue - 80).ToString(CultureInfo.InvariantCulture));
        }

        ulong current = 0;
        for (; index < content.Length; index++)
        {
            var value = content[index];
            current = (current << 7) | (uint)(value & 0x7F);
            if ((value & 0x80) != 0)
            {
                continue;
            }

            values.Add(current.ToString(CultureInfo.InvariantCulture));
            current = 0;
        }

        if (current != 0)
        {
            values.Add(current.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(".", values);
    }

    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return DefaultVersion;
        }

        return version.Trim().ToLowerInvariant();
    }

    private static string ResolveVersion(MonitoringSettings settings)
    {
        return MonitoringSettings.TryReadParameter(settings, "snmp.version", out var version) &&
            !string.IsNullOrWhiteSpace(version)
            ? NormalizeVersion(version)
            : DefaultVersion;
    }

    private static int ParseVersionCode(string version)
    {
        return NormalizeVersion(version) switch
        {
            "v1" => 0,
            "v2" or "v2c" => 1,
            "v3" => 3,
            _ => 1
        };
    }

    private static string ResolveCommunity(MonitoringSettings settings)
    {
        return MonitoringSettings.TryReadParameter(settings, "snmp.community", out var community) &&
            !string.IsNullOrWhiteSpace(community)
            ? community.Trim()
            : "public";
    }

    private static int ResolvePort(MonitoringSettings settings)
    {
        return MonitoringSettings.TryReadParameterInt(settings, "snmp.port", out var port) &&
            port is > 0 and <= 65535
            ? port
            : DefaultPort;
    }

    private static List<SnmpOidSelection> ParseOidSelections(string rawOids)
    {
        var lines = rawOids
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var selections = new List<SnmpOidSelection>(lines.Length);
        foreach (var line in lines)
        {
            var selection = ParseOidSelection(line);
            if (!string.IsNullOrWhiteSpace(selection.Oid))
            {
                selections.Add(selection with { Oid = NormalizeOid(selection.Oid) });
            }
        }

        return selections;
    }

    private static SnmpOidSelection ParseOidSelection(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return new SnmpOidSelection(string.Empty, string.Empty);
        }

        var separatorIndex = trimmed.IndexOf('|');
        if (separatorIndex < 0)
        {
            separatorIndex = trimmed.IndexOf('=');
        }

        if (separatorIndex < 0)
        {
            return new SnmpOidSelection(trimmed, string.Empty);
        }

        var oid = trimmed[..separatorIndex].Trim();
        var label = trimmed[(separatorIndex + 1)..].Trim();
        return new SnmpOidSelection(oid, label);
    }

    private static string NormalizeOid(string? oid)
    {
        if (string.IsNullOrWhiteSpace(oid))
        {
            return string.Empty;
        }

        var trimmed = oid.Trim().TrimStart('.');
        return trimmed;
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "value";
        }

        var builder = new StringBuilder(key.Length);
        foreach (var character in key.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (character is '.' or '-' or '_')
            {
                builder.Append(character);
                continue;
            }

            builder.Append('_');
        }

        var normalized = builder.ToString().Trim('.', '-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "value" : normalized;
    }

    private static bool IsUnderRoot(string oid, string rootOid)
    {
        var normalizedOid = NormalizeOid(oid);
        var normalizedRoot = NormalizeOid(rootOid);
        if (string.IsNullOrWhiteSpace(normalizedOid) || string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return false;
        }

        return string.Equals(normalizedOid, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedOid.StartsWith($"{normalizedRoot}.", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildConnectionFailureMessage(string target, int port, SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData =>
                $"Target '{target}' could not be resolved for SNMP.",
            SocketError.TimedOut =>
                $"SNMP on {target}:{port} did not respond within the timeout.",
            SocketError.ConnectionRefused =>
                $"SNMP on {target}:{port} refused the request.",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                $"SNMP on {target}:{port} is unreachable.",
            _ => $"SNMP on {target}:{port} failed: {ex.Message}"
        };
    }

    private static IReadOnlyList<SnmpDiscoveryItem> DiscoverV3(
        string target,
        MonitoringSettings settings,
        string rootOid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        target = target?.Trim() ?? string.Empty;
        var config = ResolveV3Config(settings);
        var port = ResolvePort(settings);
        var session = DiscoverV3Session(target, port, config, timeout, cancellationToken);

        var normalizedRootOid = NormalizeOid(rootOid);
        if (string.IsNullOrWhiteSpace(normalizedRootOid))
        {
            normalizedRootOid = "1.3.6.1.2.1";
        }

        var items = new List<SnmpDiscoveryItem>();
        var previousOid = normalizedRootOid;

        for (var index = 0; index < DefaultWalkLimit; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exchange = SendV3Request(
                target,
                port,
                session,
                config.SecurityLevel,
                SnmpPduType.GetNextRequest,
                [previousOid],
                timeout,
                cancellationToken);

            session = exchange.Session;
            var response = exchange.Response;

            if (response.ErrorStatus != 0)
            {
                break;
            }

            if (response.VarBinds.Count == 0)
            {
                break;
            }

            var varBind = response.VarBinds[0];
            if (!IsUnderRoot(varBind.Oid, normalizedRootOid) ||
                string.Equals(varBind.Oid, previousOid, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            items.Add(new SnmpDiscoveryItem(
                varBind.Oid,
                varBind.Value.Syntax,
                varBind.Value.DisplayValue,
                varBind.Value.NumericValue.HasValue,
                varBind.Value.NumericValue.HasValue));

            previousOid = varBind.Oid;

            if (varBind.Value.IsEndOfMib)
            {
                break;
            }
        }

        return items;
    }

    private static ValueTask<SensorExecutionResult> ExecuteV3Async(
        SensorExecutionContext context,
        CancellationToken cancellationToken)
    {
        var target = context.Target?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "target is required"));
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "snmp.oids", out var rawOids) ||
            string.IsNullOrWhiteSpace(rawOids))
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "at least one OID is required"));
        }

        var selections = ParseOidSelections(rawOids).ToArray();
        if (selections.Length == 0)
        {
            return ValueTask.FromResult(SensorExecutionResult.Critical(TimeSpan.Zero, "at least one OID is required"));
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(10);
        var port = ResolvePort(context.Settings);
        var watch = Stopwatch.StartNew();

        try
        {
            var config = ResolveV3Config(context.Settings);
            var session = DiscoverV3Session(target, port, config, timeout, cancellationToken);
            var exchange = SendV3Request(
                target,
                port,
                session,
                config.SecurityLevel,
                SnmpPduType.GetRequest,
                selections.Select(selection => selection.Oid).ToArray(),
                timeout,
                cancellationToken);

            var response = exchange.Response;

            if (response.ErrorStatus != 0)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        BuildErrorMessage(response.ErrorStatus, response.ErrorIndex)));
            }

            if (response.VarBinds.Count == 0)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(watch.Elapsed, "SNMP returned no values"));
            }

            if (response.VarBinds.Count != selections.Length)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        "SNMP response did not contain all requested OIDs"));
            }

            var channels = BuildChannels(selections, response.VarBinds);
            if (channels.Count == 0)
            {
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(watch.Elapsed, "SNMP returned no values"));
            }

            var defaultChannel = SelectDefaultChannel(channels);
            if (defaultChannel is null)
            {
                var firstChannel = channels[0];
                return ValueTask.FromResult(
                    SensorExecutionResult.Critical(
                        watch.Elapsed,
                        "SNMP returned no numeric values",
                        null,
                        firstChannel.Key,
                        MarkDefault(channels, firstChannel.Key)));
            }

            var result = SensorExecutionResult.Healthy(
                watch.Elapsed,
                BuildMessage(defaultChannel, channels.Count),
                defaultChannel.Value,
                defaultChannel.Key,
                MarkDefault(channels, defaultChannel.Key));

            return ValueTask.FromResult(SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Unknown("execution cancelled"));
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, $"execution timed out after {timeout.TotalSeconds:0.#} seconds"));
        }
        catch (SocketException ex)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, BuildConnectionFailureMessage(target, port, ex)));
        }
        catch (Exception ex)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, ex.Message));
        }
    }

    private static SnmpV3Session DiscoverV3Session(
        string target,
        int port,
        SnmpV3Config config,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var discoverySession = new SnmpV3Session(config, Array.Empty<byte>(), 0, 0, Array.Empty<byte>(), Array.Empty<byte>());
        var exchange = SendV3Request(
            target,
            port,
            discoverySession,
            SnmpSecurityLevel.NoAuthNoPriv,
            SnmpPduType.GetRequest,
            ["1.3.6.1.2.1.1.3.0"],
            timeout,
            cancellationToken);

        if (exchange.Session.EngineId.Length == 0)
        {
            throw new InvalidOperationException("SNMP v3 engine ID could not be discovered.");
        }

        return exchange.Session;
    }

    private static SnmpV3ExchangeResult SendV3Request(
        string target,
        int port,
        SnmpV3Session session,
        SnmpSecurityLevel securityLevel,
        SnmpPduType pduType,
        IReadOnlyList<string> oids,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var privacyParameters = securityLevel == SnmpSecurityLevel.AuthPriv
            ? RandomNumberGenerator.GetBytes(8)
            : Array.Empty<byte>();
        var authParameters = securityLevel == SnmpSecurityLevel.NoAuthNoPriv
            ? Array.Empty<byte>()
            : new byte[12];
        var scopedPdu = BuildV3ScopedPdu(session.EngineId, session.Config.ContextName, pduType, requestId, oids);
        var dataBytes = BuildV3DataBytes(scopedPdu, securityLevel, session, privacyParameters);
        var header = new SnmpV3Header(requestId, 65507, ResolveV3Flags(securityLevel), 3);
        var securityParameters = new SnmpV3SecurityParameters(
            session.EngineId,
            session.EngineBoots,
            session.EngineTime,
            session.Config.Username,
            authParameters,
            privacyParameters);

        var message = BuildV3MessageBytes(
            header,
            securityParameters,
            dataBytes);

        if (securityLevel != SnmpSecurityLevel.NoAuthNoPriv)
        {
            var digest = ComputeAuthenticationDigest(session.Config.AuthProtocol, session.AuthKey, message);
            authParameters = digest[..12].ToArray();
            securityParameters = securityParameters with { AuthParameters = authParameters };
            message = BuildV3MessageBytes(header, securityParameters, dataBytes);
        }

        var timeoutMs = Math.Max(1000, (int)Math.Ceiling(timeout.TotalMilliseconds));
        using var client = new UdpClient();
        client.Client.SendTimeout = timeoutMs;
        client.Client.ReceiveTimeout = timeoutMs;
        client.Connect(target, port);
        cancellationToken.ThrowIfCancellationRequested();
        client.Send(message, message.Length);

        var remote = new IPEndPoint(IPAddress.Any, 0);
        var responseBytes = client.Receive(ref remote);

        var responseParts = ParseV3Message(responseBytes);
        var responseSession = CreateV3Session(
            session.Config,
            responseParts.SecurityParameters.EngineId.Length > 0 ? responseParts.SecurityParameters.EngineId : session.EngineId,
            responseParts.SecurityParameters.EngineBoots > 0 ? responseParts.SecurityParameters.EngineBoots : session.EngineBoots,
            responseParts.SecurityParameters.EngineTime > 0 ? responseParts.SecurityParameters.EngineTime : session.EngineTime);

        if (securityLevel != SnmpSecurityLevel.NoAuthNoPriv)
        {
            VerifyV3Authentication(responseParts, responseSession);
        }

        var scopedPduBytes = (responseParts.Header.Flags & 0x02) != 0
            ? DecryptScopedPdu(responseParts.DataElement.Content, responseSession, responseParts.SecurityParameters.PrivParameters)
            : responseParts.DataElement.Content;

        var scopedReader = new BerReader(scopedPduBytes);
        var scopedElement = scopedReader.ReadElement();
        EnsureTag(scopedElement.Tag, 0x30, "SNMP scoped PDU");

        var scopedPduReader = new BerReader(scopedElement.Content);
        _ = scopedPduReader.ReadElement();
        _ = scopedPduReader.ReadElement();
        var pduElement = scopedPduReader.ReadElement();
        var response = ParsePdu(pduElement, requestId);

        return new SnmpV3ExchangeResult(response, responseSession);
    }

    private static SnmpV3Config ResolveV3Config(MonitoringSettings settings)
    {
        if (!MonitoringSettings.TryReadParameter(settings, "snmp.v3.username", out var username) ||
            string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("SNMP v3 username is required.");
        }

        var authProtocol = ResolveV3AuthProtocol(settings);
        var privProtocol = ResolveV3PrivProtocol(settings);
        var authPassword = MonitoringSettings.TryReadParameter(settings, "snmp.v3.authPassword", out var configuredAuthPassword)
            ? configuredAuthPassword
            : string.Empty;
        var privPassword = MonitoringSettings.TryReadParameter(settings, "snmp.v3.privPassword", out var configuredPrivPassword)
            ? configuredPrivPassword
            : string.Empty;
        var contextName = MonitoringSettings.TryReadParameter(settings, "snmp.v3.contextName", out var configuredContextName)
            ? configuredContextName
            : string.Empty;

        if (string.IsNullOrWhiteSpace(authPassword) && !string.IsNullOrWhiteSpace(privPassword))
        {
            authPassword = privPassword;
        }

        if (string.IsNullOrWhiteSpace(privPassword) && !string.IsNullOrWhiteSpace(authPassword))
        {
            privPassword = authPassword;
        }

        if (authProtocol != SnmpV3AuthProtocol.None && string.IsNullOrWhiteSpace(authPassword))
        {
            throw new InvalidOperationException("SNMP v3 auth password is required when authentication is enabled.");
        }

        if (privProtocol != SnmpV3PrivProtocol.None)
        {
            if (authProtocol == SnmpV3AuthProtocol.None)
            {
                throw new InvalidOperationException("SNMP v3 privacy requires authentication.");
            }

            if (string.IsNullOrWhiteSpace(privPassword))
            {
                throw new InvalidOperationException("SNMP v3 privacy password is required when privacy is enabled.");
            }
        }

        return new SnmpV3Config(
            username.Trim(),
            contextName?.Trim() ?? string.Empty,
            authProtocol,
            authPassword,
            privProtocol,
            privPassword);
    }

    private static SnmpV3Session CreateV3Session(
        SnmpV3Config config,
        byte[] engineId,
        int engineBoots,
        int engineTime)
    {
        var authKey = config.AuthProtocol == SnmpV3AuthProtocol.None
            ? Array.Empty<byte>()
            : DeriveLocalizedKey(config.AuthPassword, engineId, config.AuthProtocol);
        var privSourcePassword = string.IsNullOrWhiteSpace(config.PrivPassword) ? config.AuthPassword : config.PrivPassword;
        var privKey = config.PrivProtocol == SnmpV3PrivProtocol.None
            ? Array.Empty<byte>()
            : DeriveLocalizedKey(privSourcePassword, engineId, config.AuthProtocol);

        return new SnmpV3Session(
            config,
            engineId.ToArray(),
            engineBoots,
            engineTime,
            authKey,
            privKey);
    }

    private static SnmpV3MessageParts ParseV3Message(byte[] payload)
    {
        var reader = new BerReader(payload);
        var messageElement = reader.ReadElement();
        EnsureTag(messageElement.Tag, 0x30, "SNMP message");

        var messageReader = new BerReader(messageElement.Content);
        var version = (int)DecodeInteger(messageReader.ReadElement(), 0x02, "SNMP version");
        if (version != 3)
        {
            throw new InvalidOperationException($"Unexpected SNMP v3 message version {version}.");
        }

        var headerElement = messageReader.ReadElement();
        EnsureTag(headerElement.Tag, 0x30, "SNMP v3 header");
        var headerReader = new BerReader(headerElement.Content);
        var messageId = (int)DecodeInteger(headerReader.ReadElement(), 0x02, "SNMP v3 message id");
        var maxMessageSize = (int)DecodeInteger(headerReader.ReadElement(), 0x02, "SNMP v3 max message size");
        var flagsElement = headerReader.ReadElement();
        EnsureTag(flagsElement.Tag, 0x04, "SNMP v3 flags");
        var flags = flagsElement.Content.Length > 0 ? flagsElement.Content[0] : (byte)0;
        var securityModel = (int)DecodeInteger(headerReader.ReadElement(), 0x02, "SNMP v3 security model");
        if (securityModel != 3)
        {
            throw new InvalidOperationException($"Unexpected SNMP v3 security model {securityModel}.");
        }

        var securityElement = messageReader.ReadElement();
        EnsureTag(securityElement.Tag, 0x04, "SNMP v3 security parameters");
        var dataElement = messageReader.ReadElement();

        return new SnmpV3MessageParts(
            new SnmpV3Header(messageId, maxMessageSize, flags, securityModel),
            ParseV3SecurityParameters(securityElement.Content),
            dataElement);
    }

    private static SnmpV3SecurityParameters ParseV3SecurityParameters(byte[] payload)
    {
        var reader = new BerReader(payload);
        var sequence = reader.ReadElement();
        EnsureTag(sequence.Tag, 0x30, "SNMP v3 security parameter sequence");

        var sequenceReader = new BerReader(sequence.Content);
        var engineId = sequenceReader.ReadElement();
        EnsureTag(engineId.Tag, 0x04, "SNMP v3 engine id");
        var engineBoots = (int)DecodeInteger(sequenceReader.ReadElement(), 0x02, "SNMP v3 engine boots");
        var engineTime = (int)DecodeInteger(sequenceReader.ReadElement(), 0x02, "SNMP v3 engine time");
        var userName = DecodeOctetString(sequenceReader.ReadElement().Content.AsSpan());
        var authParameters = sequenceReader.ReadElement();
        EnsureTag(authParameters.Tag, 0x04, "SNMP v3 auth parameters");
        var privParameters = sequenceReader.ReadElement();
        EnsureTag(privParameters.Tag, 0x04, "SNMP v3 privacy parameters");

        return new SnmpV3SecurityParameters(
            engineId.Content.ToArray(),
            engineBoots,
            engineTime,
            userName,
            authParameters.Content.ToArray(),
            privParameters.Content.ToArray());
    }

    private static void VerifyV3Authentication(SnmpV3MessageParts parts, SnmpV3Session session)
    {
        if (session.Config.SecurityLevel == SnmpSecurityLevel.NoAuthNoPriv)
        {
            return;
        }

        var zeroAuth = parts.SecurityParameters with
        {
            AuthParameters = new byte[parts.SecurityParameters.AuthParameters.Length]
        };

        var reconstructed = BuildV3MessageBytes(
            parts.Header,
            zeroAuth,
            EncodeTagged(parts.DataElement.Tag, parts.DataElement.Content));

        var digest = ComputeAuthenticationDigest(session.Config.AuthProtocol, session.AuthKey, reconstructed);
        var expected = parts.SecurityParameters.AuthParameters;
        if (expected.Length < 12 || !expected.Take(12).SequenceEqual(digest.Take(12)))
        {
            throw new InvalidOperationException("SNMP v3 authentication failed.");
        }
    }

    private static byte[] BuildV3MessageBytes(
        SnmpV3Header header,
        SnmpV3SecurityParameters securityParameters,
        byte[] dataBytes)
    {
        var content = new List<byte>();
        content.AddRange(EncodeInteger(3));
        content.AddRange(BuildV3HeaderBytes(header));
        content.AddRange(EncodeOctetString(BuildV3SecurityParametersBytes(securityParameters)));
        content.AddRange(dataBytes);
        return EncodeSequence(content.ToArray());
    }

    private static byte[] BuildV3HeaderBytes(SnmpV3Header header)
    {
        var content = new List<byte>();
        content.AddRange(EncodeInteger(header.MessageId));
        content.AddRange(EncodeInteger(header.MaxMessageSize));
        content.AddRange(EncodeOctetString(new[] { header.Flags }));
        content.AddRange(EncodeInteger(header.SecurityModel));
        return EncodeSequence(content.ToArray());
    }

    private static byte[] BuildV3SecurityParametersBytes(SnmpV3SecurityParameters securityParameters)
    {
        var content = new List<byte>();
        content.AddRange(EncodeOctetString(securityParameters.EngineId));
        content.AddRange(EncodeInteger(securityParameters.EngineBoots));
        content.AddRange(EncodeInteger(securityParameters.EngineTime));
        content.AddRange(EncodeOctetString(Encoding.UTF8.GetBytes(securityParameters.UserName ?? string.Empty)));
        content.AddRange(EncodeOctetString(securityParameters.AuthParameters));
        content.AddRange(EncodeOctetString(securityParameters.PrivParameters));
        return EncodeSequence(content.ToArray());
    }

    private static byte[] BuildV3ScopedPdu(byte[] engineId, string contextName, SnmpPduType pduType, int requestId, IReadOnlyList<string> oids)
    {
        var content = new List<byte>();
        content.AddRange(EncodeOctetString(engineId));
        content.AddRange(EncodeOctetString(Encoding.UTF8.GetBytes(contextName ?? string.Empty)));
        content.AddRange(BuildPdu(pduType, requestId, oids));
        return EncodeSequence(content.ToArray());
    }

    private static byte ResolveV3Flags(SnmpSecurityLevel securityLevel)
    {
        return securityLevel switch
        {
            SnmpSecurityLevel.NoAuthNoPriv => 0x04,
            SnmpSecurityLevel.AuthNoPriv => 0x05,
            _ => 0x07
        };
    }

    private static byte[] BuildV3DataBytes(
        byte[] scopedPdu,
        SnmpSecurityLevel securityLevel,
        SnmpV3Session session,
        byte[] privacyParameters)
    {
        return securityLevel == SnmpSecurityLevel.AuthPriv
            ? EncodeOctetString(EncryptScopedPdu(scopedPdu, session, privacyParameters))
            : EncodeTagged(0xA0, scopedPdu);
    }

    private static SnmpSecurityLevel ResolveSecurityLevel(SnmpV3Config config)
    {
        return config.AuthProtocol == SnmpV3AuthProtocol.None
            ? SnmpSecurityLevel.NoAuthNoPriv
            : config.PrivProtocol == SnmpV3PrivProtocol.None
                ? SnmpSecurityLevel.AuthNoPriv
                : SnmpSecurityLevel.AuthPriv;
    }

    private static SnmpV3AuthProtocol ResolveV3AuthProtocol(MonitoringSettings settings)
    {
        if (!MonitoringSettings.TryReadParameter(settings, "snmp.v3.authProtocol", out var protocol) ||
            string.IsNullOrWhiteSpace(protocol))
        {
            return SnmpV3AuthProtocol.Sha1;
        }

        return protocol.Trim().ToLowerInvariant() switch
        {
            "none" => SnmpV3AuthProtocol.None,
            "md5" => SnmpV3AuthProtocol.Md5,
            "sha1" => SnmpV3AuthProtocol.Sha1,
            _ => throw new InvalidOperationException($"Unsupported SNMP v3 auth protocol '{protocol}'.")
        };
    }

    private static SnmpV3PrivProtocol ResolveV3PrivProtocol(MonitoringSettings settings)
    {
        if (!MonitoringSettings.TryReadParameter(settings, "snmp.v3.privProtocol", out var protocol) ||
            string.IsNullOrWhiteSpace(protocol))
        {
            return SnmpV3PrivProtocol.None;
        }

        return protocol.Trim().ToLowerInvariant() switch
        {
            "none" => SnmpV3PrivProtocol.None,
            "des" => SnmpV3PrivProtocol.Des,
            "aes128" => SnmpV3PrivProtocol.Aes128,
            _ => throw new InvalidOperationException($"Unsupported SNMP v3 privacy protocol '{protocol}'.")
        };
    }

    private static byte[] ComputeAuthenticationDigest(SnmpV3AuthProtocol protocol, byte[] key, byte[] message)
    {
        return protocol switch
        {
            SnmpV3AuthProtocol.Md5 => ComputeHmacMD5(key, message),
            SnmpV3AuthProtocol.Sha1 => ComputeHmacSHA1(key, message),
            SnmpV3AuthProtocol.None => Array.Empty<byte>(),
            _ => throw new InvalidOperationException("Unsupported SNMP v3 authentication protocol.")
        };
    }

    private static byte[] ComputeHmacMD5(byte[] key, byte[] message)
    {
        using var hmac = new HMACMD5(key);
        return hmac.ComputeHash(message);
    }

    private static byte[] ComputeHmacSHA1(byte[] key, byte[] message)
    {
        using var hmac = new HMACSHA1(key);
        return hmac.ComputeHash(message);
    }

    private static byte[] DeriveLocalizedKey(string passphrase, byte[] engineId, SnmpV3AuthProtocol protocol)
    {
        var algorithmName = protocol switch
        {
            SnmpV3AuthProtocol.Md5 => HashAlgorithmName.MD5,
            SnmpV3AuthProtocol.Sha1 => HashAlgorithmName.SHA1,
            _ => throw new InvalidOperationException("SNMP v3 key localization requires an authentication algorithm.")
        };

        var ku = DerivePasswordKey(passphrase, algorithmName);
        using var hash = IncrementalHash.CreateHash(algorithmName);
        hash.AppendData(ku);
        hash.AppendData(engineId);
        hash.AppendData(ku);
        return hash.GetHashAndReset();
    }

    private static byte[] DerivePasswordKey(string passphrase, HashAlgorithmName algorithmName)
    {
        var passBytes = Encoding.UTF8.GetBytes(passphrase);
        if (passBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var hash = IncrementalHash.CreateHash(algorithmName);
        var remaining = 1_048_576;
        while (remaining > 0)
        {
            var chunk = Math.Min(passBytes.Length, remaining);
            hash.AppendData(passBytes.AsSpan(0, chunk));
            remaining -= chunk;
        }

        return hash.GetHashAndReset();
    }

    private static byte[] DecryptScopedPdu(byte[] ciphertext, SnmpV3Session session, byte[] privParameters)
    {
        return session.Config.PrivProtocol switch
        {
            SnmpV3PrivProtocol.Des => DecryptDes(ciphertext, session, privParameters),
            SnmpV3PrivProtocol.Aes128 => DecryptAes128(ciphertext, session, privParameters),
            SnmpV3PrivProtocol.None => ciphertext,
            _ => throw new InvalidOperationException("Unsupported SNMP v3 privacy protocol.")
        };
    }

    private static byte[] EncryptScopedPdu(byte[] plaintext, SnmpV3Session session, byte[] privParameters)
    {
        return session.Config.PrivProtocol switch
        {
            SnmpV3PrivProtocol.Des => EncryptDes(plaintext, session, privParameters),
            SnmpV3PrivProtocol.Aes128 => EncryptAes128(plaintext, session, privParameters),
            SnmpV3PrivProtocol.None => plaintext,
            _ => throw new InvalidOperationException("Unsupported SNMP v3 privacy protocol.")
        };
    }

    private static byte[] EncryptAes128(byte[] plaintext, SnmpV3Session session, byte[] privParameters)
    {
        if (session.PrivKey.Length < 16)
        {
            throw new InvalidOperationException("SNMP v3 AES privacy key is too short.");
        }

        if (privParameters.Length != 8)
        {
            throw new InvalidOperationException("SNMP v3 AES privacy parameters must be 8 bytes.");
        }

        var iv = BuildAesIv(session.EngineBoots, session.EngineTime, privParameters);
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CFB;
        aes.FeedbackSize = 128;
        aes.Padding = PaddingMode.None;
        aes.Key = session.PrivKey.Take(16).ToArray();
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] EncryptDes(byte[] plaintext, SnmpV3Session session, byte[] privParameters)
    {
        if (session.PrivKey.Length < 16)
        {
            throw new InvalidOperationException("SNMP v3 DES privacy key is too short.");
        }

        if (privParameters.Length != 8)
        {
            throw new InvalidOperationException("SNMP v3 DES privacy parameters must be 8 bytes.");
        }

        var keyMaterial = session.PrivKey.Take(16).ToArray();
        var desKey = AdjustDesParity(keyMaterial.Take(8).ToArray());
        var preIv = keyMaterial.Skip(8).Take(8).ToArray();
        var iv = XorBytes(preIv, privParameters);
        var padded = PadZeroToBlockSize(plaintext, 8);

        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.None;
        des.Key = desKey;
        des.IV = iv;

        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(padded, 0, padded.Length);
    }

    private static byte[] DecryptAes128(byte[] ciphertext, SnmpV3Session session, byte[] privParameters)
    {
        if (session.PrivKey.Length < 16)
        {
            throw new InvalidOperationException("SNMP v3 AES privacy key is too short.");
        }

        if (privParameters.Length != 8)
        {
            throw new InvalidOperationException("SNMP v3 AES privacy parameters must be 8 bytes.");
        }

        var iv = BuildAesIv(session.EngineBoots, session.EngineTime, privParameters);
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CFB;
        aes.FeedbackSize = 128;
        aes.Padding = PaddingMode.None;
        aes.Key = session.PrivKey.Take(16).ToArray();
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static byte[] DecryptDes(byte[] ciphertext, SnmpV3Session session, byte[] privParameters)
    {
        if (session.PrivKey.Length < 16)
        {
            throw new InvalidOperationException("SNMP v3 DES privacy key is too short.");
        }

        if (privParameters.Length != 8)
        {
            throw new InvalidOperationException("SNMP v3 DES privacy parameters must be 8 bytes.");
        }

        var keyMaterial = session.PrivKey.Take(16).ToArray();
        var desKey = AdjustDesParity(keyMaterial.Take(8).ToArray());
        var preIv = keyMaterial.Skip(8).Take(8).ToArray();
        var iv = XorBytes(preIv, privParameters);

        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.None;
        des.Key = desKey;
        des.IV = iv;

        using var decryptor = des.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static byte[] BuildAesIv(int engineBoots, int engineTime, byte[] privParameters)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(0, 4), engineBoots);
        BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(4, 4), engineTime);
        Buffer.BlockCopy(privParameters, 0, iv, 8, 8);
        return iv;
    }

    private static byte[] AdjustDesParity(byte[] key)
    {
        var adjusted = new byte[8];
        for (var index = 0; index < adjusted.Length; index++)
        {
            var value = key[index];
            var parity = 0;
            for (var bit = 1; bit < 8; bit++)
            {
                parity ^= (value >> bit) & 1;
            }

            var lsb = parity == 0 ? (byte)1 : (byte)0;
            adjusted[index] = (byte)((value & 0xFE) | lsb);
        }

        return adjusted;
    }

    private static byte[] XorBytes(byte[] left, byte[] right)
    {
        var result = new byte[Math.Min(left.Length, right.Length)];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(left[index] ^ right[index]);
        }

        return result;
    }

    private static byte[] PadZeroToBlockSize(byte[] value, int blockSize)
    {
        if (blockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        var remainder = value.Length % blockSize;
        if (remainder == 0)
        {
            return value;
        }

        var padded = new byte[value.Length + (blockSize - remainder)];
        Buffer.BlockCopy(value, 0, padded, 0, value.Length);
        return padded;
    }

    private sealed record SnmpV3Config(
        string Username,
        string ContextName,
        SnmpV3AuthProtocol AuthProtocol,
        string AuthPassword,
        SnmpV3PrivProtocol PrivProtocol,
        string PrivPassword)
    {
        public SnmpSecurityLevel SecurityLevel => AuthProtocol == SnmpV3AuthProtocol.None
            ? SnmpSecurityLevel.NoAuthNoPriv
            : PrivProtocol == SnmpV3PrivProtocol.None
                ? SnmpSecurityLevel.AuthNoPriv
                : SnmpSecurityLevel.AuthPriv;
    }

    private sealed record SnmpV3Session(
        SnmpV3Config Config,
        byte[] EngineId,
        int EngineBoots,
        int EngineTime,
        byte[] AuthKey,
        byte[] PrivKey);

    private sealed record SnmpV3Header(int MessageId, int MaxMessageSize, byte Flags, int SecurityModel);

    private sealed record SnmpV3SecurityParameters(
        byte[] EngineId,
        int EngineBoots,
        int EngineTime,
        string UserName,
        byte[] AuthParameters,
        byte[] PrivParameters);

    private sealed record SnmpV3MessageParts(
        SnmpV3Header Header,
        SnmpV3SecurityParameters SecurityParameters,
        BerElement DataElement);

    private sealed record SnmpV3ExchangeResult(
        SnmpResponse Response,
        SnmpV3Session Session);

    private static bool TryParseNumber(string? rawValue, out double value)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            value = default;
            return false;
        }

        return double.TryParse(rawValue.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatNumber(double value)
    {
        return value % 1 == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static byte[] EncodeSequence(byte[] content)
    {
        return EncodeTagged(0x30, content);
    }

    private static byte[] EncodeTagged(byte tag, byte[] content)
    {
        var output = new List<byte>(content.Length + 8)
        {
            tag
        };
        output.AddRange(EncodeLength(content.Length));
        output.AddRange(content);
        return output.ToArray();
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length < 128)
        {
            return [ (byte)length ];
        }

        var buffer = new List<byte>();
        var remaining = length;
        while (remaining > 0)
        {
            buffer.Insert(0, (byte)(remaining & 0xFF));
            remaining >>= 8;
        }

        buffer.Insert(0, (byte)(0x80 | buffer.Count));
        return buffer.ToArray();
    }

    private static byte[] EncodeInteger(long value)
    {
        var bytes = new List<byte>();
        var remaining = value;
        do
        {
            bytes.Insert(0, (byte)(remaining & 0xFF));
            remaining >>= 8;
        }
        while (remaining != 0 && remaining != -1);

        if (value >= 0 && bytes.Count > 0 && (bytes[0] & 0x80) != 0)
        {
            bytes.Insert(0, 0x00);
        }
        else if (value < 0 && bytes.Count > 0 && (bytes[0] & 0x80) == 0)
        {
            bytes.Insert(0, 0xFF);
        }

        return EncodeTagged(0x02, bytes.ToArray());
    }

    private static byte[] EncodeOctetString(byte[] bytes)
    {
        return EncodeTagged(0x04, bytes);
    }

    private static byte[] EncodeNull()
    {
        return [0x05, 0x00];
    }

    private static byte[] EncodeObjectIdentifier(string oid)
    {
        var normalizedOid = NormalizeOid(oid);
        var parts = normalizedOid
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    throw new InvalidOperationException($"Invalid OID '{oid}'.");
                }

                return value;
            })
            .ToArray();

        if (parts.Length < 2)
        {
            throw new InvalidOperationException($"Invalid OID '{oid}'.");
        }

        if (parts[0] > 2 || (parts[0] < 2 && parts[1] > 39))
        {
            throw new InvalidOperationException($"Invalid OID '{oid}'.");
        }

        var output = new List<byte>();
        output.AddRange(EncodeBase128(parts[0] * 40UL + parts[1]));

        for (var index = 2; index < parts.Length; index++)
        {
            output.AddRange(EncodeBase128(parts[index]));
        }

        return EncodeTagged(0x06, output.ToArray());
    }

    private static IEnumerable<byte> EncodeBase128(ulong value)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        value >>= 7;

        while (value > 0)
        {
            stack.Push((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        return stack;
    }

    private static void EnsureTag(byte actualTag, byte expectedTag, string label)
    {
        if (actualTag != expectedTag)
        {
            throw new InvalidOperationException($"Unexpected {label} tag 0x{actualTag:X2}.");
        }
    }

    private sealed class BerReader
    {
        private readonly ReadOnlyMemory<byte> _data;
        private int _offset;

        public BerReader(ReadOnlyMemory<byte> data)
        {
            _data = data;
        }

        public bool TryReadElement(out BerElement element)
        {
            if (_offset >= _data.Length)
            {
                element = default;
                return false;
            }

            element = ReadElement();
            return true;
        }

        public BerElement ReadElement()
        {
            if (_offset >= _data.Length)
            {
                throw new InvalidOperationException("Unexpected end of SNMP payload.");
            }

            var tag = _data.Span[_offset++];
            var length = ReadLength();
            if (_offset + length > _data.Length)
            {
                throw new InvalidOperationException("Invalid SNMP payload length.");
            }

            var content = _data.Slice(_offset, length).ToArray();
            _offset += length;
            return new BerElement(tag, content);
        }

        private int ReadLength()
        {
            if (_offset >= _data.Length)
            {
                throw new InvalidOperationException("Unexpected end of SNMP payload.");
            }

            var first = _data.Span[_offset++];
            if ((first & 0x80) == 0)
            {
                return first;
            }

            var count = first & 0x7F;
            if (count == 0)
            {
                throw new InvalidOperationException("Indefinite SNMP lengths are not supported.");
            }

            if (_offset + count > _data.Length)
            {
                throw new InvalidOperationException("Invalid SNMP length field.");
            }

            var length = 0;
            for (var index = 0; index < count; index++)
            {
                length = (length << 8) | _data.Span[_offset++];
            }

            return length;
        }
    }
}

public sealed record SnmpDiscoveryItem(
    string Oid,
    string Syntax,
    string Value,
    bool IsNumeric,
    bool SelectedByDefault);

internal enum SnmpPduType : byte
{
    GetRequest = 0,
    GetNextRequest = 1
}

internal enum SnmpSecurityLevel
{
    NoAuthNoPriv = 0,
    AuthNoPriv = 1,
    AuthPriv = 2
}

internal enum SnmpV3AuthProtocol
{
    None = 0,
    Md5 = 1,
    Sha1 = 2
}

internal enum SnmpV3PrivProtocol
{
    None = 0,
    Des = 1,
    Aes128 = 2
}

internal sealed record SnmpOidSelection(string Oid, string Label);

internal sealed record SnmpVarBind(string Oid, SnmpValue Value);

internal sealed record SnmpValue(
    string Syntax,
    string DisplayValue,
    double? NumericValue,
    string? Unit,
    bool IsEndOfMib);

internal sealed record SnmpResponse(
    int ErrorStatus,
    int ErrorIndex,
    IReadOnlyList<SnmpVarBind> VarBinds);

internal readonly record struct BerElement(byte Tag, byte[] Content);
