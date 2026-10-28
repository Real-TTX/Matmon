using System.Globalization;
using System.Net;
using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Core.SnmpTraps;

/// <summary>
/// How the primary processes SNMP traps - which communities count, the SNMPv3 users, whether a trap polls the device's
/// sensors - edited under System > SNMP traps, stored in workspace.json. Receiving them is the SNMP trap collector plugin's
/// job (on the main probe or on a probe near the devices); every collector's traps are processed with these settings.
/// </summary>
public sealed class SnmpTrapSettings
{
    public const int DefaultPort = 162;

    /// <summary>Before migration 9 the primary had a trap socket of its own; <see cref="Enabled"/>, <see cref="Port"/> and
    /// <see cref="AllowedSources"/> are what it used. The migration turns a receiver that was on into the trap collector
    /// plugin on the main probe and clears the switch; receiving is the plugin's job, the rest of this class is how the
    /// primary processes what any collector took.</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    /// <summary>IP addresses and CIDR ranges allowed to send, separated by commas, spaces or new lines. Empty = anyone who
    /// can reach the port.</summary>
    public string? AllowedSources { get; set; }

    /// <summary>Communities a v1/v2c trap must carry, separated by commas or new lines. Empty = any community. A community
    /// is no real secret (it travels in clear text), but it keeps a stray trap from another network out.</summary>
    public string? Communities { get; set; }

    /// <summary>DataProtection ciphertext of <see cref="Communities"/> - what persists (the communities are the passwords of
    /// v1/v2c traps; the plaintext is cleared before the workspace is written).</summary>
    public string? ProtectedCommunities { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CommunitiesHydrationFailed { get; set; }

    /// <summary>A trap from a device makes its sensors poll at once (they would otherwise only notice at their next poll).
    /// On by default: a UPS that sends "on battery" should be red within seconds, not 30 s later.</summary>
    public bool PollOnTrap { get; set; } = true;

    /// <summary>SNMPv3 users a v3 trap may come from (USM: authentication and privacy keyed to the SENDER's engine id).
    /// Their passwords are encrypted at rest like every other secret.</summary>
    public List<SnmpTrapV3User> V3Users { get; set; } = [];

    public const int MaxV3Users = 10;

    public SnmpTrapSettings Clone() => new()
    {
        Enabled = Enabled,
        Port = Port,
        AllowedSources = AllowedSources,
        Communities = Communities,
        ProtectedCommunities = ProtectedCommunities,
        CommunitiesHydrationFailed = CommunitiesHydrationFailed,
        PollOnTrap = PollOnTrap,
        V3Users = V3Users.Select(user => user.Clone()).ToList()
    };

    public SnmpTrapSettings Normalized()
    {
        var copy = Clone();
        copy.Port = copy.Port is >= 1 and <= 65535 ? copy.Port : DefaultPort;
        copy.AllowedSources = string.IsNullOrWhiteSpace(copy.AllowedSources) ? null : copy.AllowedSources.Trim();
        copy.Communities = string.IsNullOrWhiteSpace(copy.Communities) ? null : copy.Communities.Trim();
        copy.V3Users = copy.V3Users
            .Where(user => !string.IsNullOrWhiteSpace(user.Username))
            .Select(user =>
            {
                user.Username = user.Username.Trim();
                user.AuthProtocol = SnmpV3Crypto.ParseAuth(user.AuthProtocol) is null ? "sha1" : (user.AuthProtocol ?? "sha1").Trim().ToLowerInvariant();
                user.PrivProtocol = SnmpV3Crypto.ParsePriv(user.PrivProtocol) is null || SnmpV3Crypto.ParseAuth(user.AuthProtocol) == SnmpV3AuthProtocol.None ? "none" : (user.PrivProtocol ?? "none").Trim().ToLowerInvariant();
                return user;
            })
            .DistinctBy(user => user.Username, StringComparer.Ordinal)
            .Take(MaxV3Users)
            .ToList();
        return copy;
    }

    /// <summary>The communities as a list (empty = any).</summary>
    public IReadOnlyList<string> CommunityList() =>
        (Communities ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Everything that decides which socket is open and who may use it - a change means a new listener.</summary>
    public string ListenerSignature() => $"{Enabled}|{Port}|{AllowedSources?.Trim()}";
}

/// <summary>One SNMPv3 user of the trap receiver. The passwords live in memory in the clear and are written to
/// workspace.json only DataProtection-encrypted (<see cref="ProtectedAuthPassword"/> / <see cref="ProtectedPrivPassword"/>).</summary>
public sealed class SnmpTrapV3User
{
    public string Username { get; set; } = string.Empty;

    /// <summary>none, md5, sha1, sha224, sha256, sha384, sha512 (<see cref="SnmpV3Crypto.ParseAuth"/>).</summary>
    public string AuthProtocol { get; set; } = "sha1";

    public string? AuthPassword { get; set; }

    public string? ProtectedAuthPassword { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool AuthPasswordHydrationFailed { get; set; }

    /// <summary>none, des, 3des, aes128, aes192, aes256, aes192c, aes256c (<see cref="SnmpV3Crypto.ParsePriv"/>).</summary>
    public string PrivProtocol { get; set; } = "none";

    public string? PrivPassword { get; set; }

    public string? ProtectedPrivPassword { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool PrivPasswordHydrationFailed { get; set; }

    public SnmpTrapV3User Clone() => new()
    {
        Username = Username,
        AuthProtocol = AuthProtocol,
        AuthPassword = AuthPassword,
        ProtectedAuthPassword = ProtectedAuthPassword,
        AuthPasswordHydrationFailed = AuthPasswordHydrationFailed,
        PrivProtocol = PrivProtocol,
        PrivPassword = PrivPassword,
        ProtectedPrivPassword = ProtectedPrivPassword,
        PrivPasswordHydrationFailed = PrivPasswordHydrationFailed
    };
}

public enum SnmpTrapPduType
{
    TrapV1 = 0,
    TrapV2 = 1,
    Inform = 2
}

/// <summary>One variable binding of a trap: the OID, its syntax and the value as text.</summary>
public sealed record SnmpTrapVarBind(string Oid, string Syntax, string Value, double? Number);

/// <summary>
/// A received trap, version-independent. <see cref="TrapOid"/> is what the trap IS: SNMPv2's snmpTrapOID.0, or for an
/// SNMPv1 trap the OID RFC 3584 maps it to (a generic trap 0-5 = 1.3.6.1.6.3.1.1.5.(n+1), an enterprise-specific one =
/// enterprise.0.specific) - so a v1 linkDown and a v2 linkDown are the same OID.
/// </summary>
public sealed record SnmpTrap(
    int Version,
    string Community,
    SnmpTrapPduType PduType,
    int RequestId,
    string TrapOid,
    TimeSpan? Uptime,
    string? AgentAddress,
    IReadOnlyList<SnmpTrapVarBind> VarBinds);

/// <summary>
/// Reads SNMPv1 traps and SNMPv2c traps / informs (RFC 1157, RFC 3416) - with the BER reader the SNMP sensor uses. An
/// SNMPv3 trap is recognised and refused in words (its USM part needs the sender's engine id and keys, not built yet).
/// Pure: the receiver does sockets, this does bytes, and the bytes are what the tests pin.
/// </summary>
public static class SnmpTrapParser
{
    public const string SysUpTimeOid = "1.3.6.1.2.1.1.3.0";
    public const string SnmpTrapOidOid = "1.3.6.1.6.3.1.1.4.1.0";

    /// <summary>snmpTrapAddress.0 - a relay that forwards a trap puts the original device's address here.</summary>
    public const string SnmpTrapAddressOid = "1.3.6.1.6.3.18.1.3.0";
    private const string GenericTrapPrefix = "1.3.6.1.6.3.1.1.5.";

    public static bool TryParse(ReadOnlySpan<byte> datagram, out SnmpTrap? trap, out string? error) =>
        TryParse(datagram, [], out trap, out error);

    /// <param name="v3Users">The users an SNMPv3 trap is checked against; without any, a v3 trap is refused.</param>
    public static bool TryParse(ReadOnlySpan<byte> datagram, IReadOnlyList<SnmpTrapV3User> v3Users, out SnmpTrap? trap, out string? error)
    {
        trap = null;
        error = null;
        try
        {
            var reader = new SnmpSensorExecutor.BerReader(datagram.ToArray());
            var message = reader.ReadElement();
            SnmpSensorExecutor.EnsureTag(message.Tag, 0x30, "SNMP message");
            var fields = new SnmpSensorExecutor.BerReader(message.Content);
            var versionElement = fields.ReadElement();
            SnmpSensorExecutor.EnsureTag(versionElement.Tag, 0x02, "SNMP version");
            var version = SnmpSensorExecutor.DecodeSignedInteger(versionElement.Content);
            if (version == 3)
            {
                return TryParseV3(datagram.ToArray(), v3Users, out trap, out error);
            }

            if (version is not (0 or 1))
            {
                error = $"Unknown SNMP version {version}.";
                return false;
            }

            var communityElement = fields.ReadElement();
            SnmpSensorExecutor.EnsureTag(communityElement.Tag, 0x04, "SNMP community");
            var community = Encoding.UTF8.GetString(communityElement.Content);
            var pdu = fields.ReadElement();
            trap = pdu.Tag switch
            {
                0xA4 => ParseV1(community, pdu.Content),
                0xA7 => ParseV2(community, pdu.Content, SnmpTrapPduType.TrapV2),
                0xA6 => ParseV2(community, pdu.Content, SnmpTrapPduType.Inform),
                _ => null
            };

            if (trap is null)
            {
                error = $"Not a trap (PDU type 0x{pdu.Tag:X2}).";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IndexOutOfRangeException or System.Security.Cryptography.CryptographicException)
        {
            error = $"Not a readable SNMP message: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// An SNMPv3 trap (RFC 3414 USM). The sender of a trap is the authoritative engine, so the keys are localised with
    /// the engine id the MESSAGE carries; the user and its protocols come from the settings. The authentication is the
    /// HMAC over the whole message with the auth parameters zeroed, the privacy part decrypts the ScopedPDU. A message
    /// asking for less security than its user is configured for is refused (no downgrade), and so is an inform - its
    /// receiver would have to be the authoritative engine, which needs engine-id discovery. Not checked: the time
    /// window against replays (the receiver keeps no notion of each sender's engine time yet).
    /// </summary>
    private static bool TryParseV3(byte[] datagram, IReadOnlyList<SnmpTrapV3User> users, out SnmpTrap? trap, out string? error)
    {
        trap = null;
        if (users.Count == 0)
        {
            error = "An SNMPv3 trap arrived, but no SNMPv3 user is set up under System > SNMP traps.";
            return false;
        }

        var parts = SnmpSensorExecutor.ParseV3Message(datagram);
        var security = parts.SecurityParameters;
        var user = users.FirstOrDefault(candidate => string.Equals(candidate.Username, security.UserName, StringComparison.Ordinal));
        if (user is null)
        {
            error = $"An SNMPv3 trap from the unknown user '{security.UserName}'.";
            return false;
        }

        var auth = SnmpV3Crypto.ParseAuth(user.AuthProtocol) ?? SnmpV3AuthProtocol.None;
        var priv = SnmpV3Crypto.ParsePriv(user.PrivProtocol) ?? SnmpV3PrivProtocol.None;
        var hasAuth = (parts.Header.Flags & 0x01) != 0;
        var hasPriv = (parts.Header.Flags & 0x02) != 0;
        if ((auth != SnmpV3AuthProtocol.None && !hasAuth) || (priv != SnmpV3PrivProtocol.None && !hasPriv))
        {
            error = $"The SNMPv3 trap from '{user.Username}' is less protected than the user is set up for (authentication/privacy) - refused.";
            return false;
        }

        if (hasAuth)
        {
            if (auth == SnmpV3AuthProtocol.None)
            {
                error = $"The SNMPv3 trap from '{user.Username}' is authenticated, but the user has no authentication protocol set up.";
                return false;
            }

            var key = SnmpV3Crypto.LocalizedKey(auth, user.AuthPassword ?? string.Empty, security.EngineId);
            var zeroed = security with { AuthParameters = new byte[security.AuthParameters.Length] };
            var reconstructed = SnmpSensorExecutor.BuildV3MessageBytes(parts.Header, zeroed, SnmpSensorExecutor.EncodeTagged(parts.DataElement.Tag, parts.DataElement.Content));
            var digest = SnmpV3Crypto.Mac(auth, key, reconstructed);
            if (digest.Length != security.AuthParameters.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(digest, security.AuthParameters))
            {
                error = $"The SNMPv3 trap from '{user.Username}' failed authentication - wrong password or protocol.";
                return false;
            }
        }

        byte[] scoped;
        if (hasPriv)
        {
            if (priv == SnmpV3PrivProtocol.None || auth == SnmpV3AuthProtocol.None)
            {
                error = $"The SNMPv3 trap from '{user.Username}' is encrypted, but the user has no privacy protocol set up.";
                return false;
            }

            SnmpSensorExecutor.EnsureTag(parts.DataElement.Tag, 0x04, "SNMP v3 encrypted PDU");
            var privKey = SnmpV3Crypto.PrivacyKey(auth, priv, string.IsNullOrEmpty(user.PrivPassword) ? user.AuthPassword ?? string.Empty : user.PrivPassword, security.EngineId);
            scoped = SnmpV3Crypto.Decrypt(priv, privKey, security.EngineBoots, security.EngineTime, security.PrivParameters, parts.DataElement.Content);
        }
        else
        {
            scoped = SnmpSensorExecutor.EncodeTagged(parts.DataElement.Tag, parts.DataElement.Content);
        }

        // ScopedPDU ::= SEQUENCE { contextEngineID, contextName, data }. A decrypted one may carry padding after it.
        var scopedReader = new SnmpSensorExecutor.BerReader(scoped);
        var scopedPdu = scopedReader.ReadElement();
        SnmpSensorExecutor.EnsureTag(scopedPdu.Tag, 0x30, "SNMP v3 scoped PDU");
        var fields = new SnmpSensorExecutor.BerReader(scopedPdu.Content);
        _ = fields.ReadElement(); // contextEngineID
        _ = fields.ReadElement(); // contextName
        var pdu = fields.ReadElement();
        if (pdu.Tag == 0xA6)
        {
            error = "SNMPv3 informs are not supported - set the device to send v3 traps (or v2c informs).";
            return false;
        }

        if (pdu.Tag != 0xA7)
        {
            error = $"Not a trap (PDU type 0x{pdu.Tag:X2}).";
            return false;
        }

        var v2 = ParseV2(user.Username, pdu.Content, SnmpTrapPduType.TrapV2);
        trap = v2 with { Version = 3 };
        error = null;
        return true;
    }

    /// <summary>An SNMPv3 trap as a device sends it - for the tests (and the interop check with Net-SNMP).</summary>
    public static byte[] BuildV3Trap(SnmpTrapV3User user, byte[] engineId, int engineBoots, int engineTime, string trapOid, TimeSpan uptime, IEnumerable<(string Oid, string Value)> values, byte[]? privSalt = null)
    {
        var auth = SnmpV3Crypto.ParseAuth(user.AuthProtocol) ?? SnmpV3AuthProtocol.None;
        var priv = SnmpV3Crypto.ParsePriv(user.PrivProtocol) ?? SnmpV3PrivProtocol.None;

        // The v2 trap PDU, taken out of a v2c message built the usual way.
        var v2 = new SnmpSensorExecutor.BerReader(BuildV2Trap("x", trapOid, uptime, values));
        var v2Fields = new SnmpSensorExecutor.BerReader(v2.ReadElement().Content);
        _ = v2Fields.ReadElement();
        _ = v2Fields.ReadElement();
        var pdu = v2Fields.ReadElement();

        var scoped = new List<byte>();
        scoped.AddRange(SnmpSensorExecutor.EncodeOctetString(engineId));
        scoped.AddRange(SnmpSensorExecutor.EncodeOctetString([]));
        scoped.AddRange(SnmpSensorExecutor.EncodeTagged(pdu.Tag, pdu.Content));
        var scopedPdu = SnmpSensorExecutor.EncodeSequence(scoped.ToArray());

        var salt = priv == SnmpV3PrivProtocol.None ? [] : privSalt ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        var data = priv == SnmpV3PrivProtocol.None
            ? scopedPdu
            : SnmpSensorExecutor.EncodeOctetString(SnmpV3Crypto.Encrypt(priv, SnmpV3Crypto.PrivacyKey(auth, priv, string.IsNullOrEmpty(user.PrivPassword) ? user.AuthPassword ?? string.Empty : user.PrivPassword, engineId), engineBoots, engineTime, salt, scopedPdu));
        var flags = (byte)(auth == SnmpV3AuthProtocol.None ? 0x00 : priv == SnmpV3PrivProtocol.None ? 0x01 : 0x03);
        var header = new SnmpSensorExecutor.SnmpV3Header(4711, 65507, flags, 3);
        var authLength = auth == SnmpV3AuthProtocol.None ? 0 : SnmpV3Crypto.MacLength(auth);
        var security = new SnmpSensorExecutor.SnmpV3SecurityParameters(engineId, engineBoots, engineTime, user.Username, new byte[authLength], salt);
        var message = SnmpSensorExecutor.BuildV3MessageBytes(header, security, data);
        if (auth == SnmpV3AuthProtocol.None)
        {
            return message;
        }

        var mac = SnmpV3Crypto.Mac(auth, SnmpV3Crypto.LocalizedKey(auth, user.AuthPassword ?? string.Empty, engineId), message);
        return SnmpSensorExecutor.BuildV3MessageBytes(header, security with { AuthParameters = mac }, data);
    }

    private static SnmpTrap ParseV1(string community, byte[] content)
    {
        var reader = new SnmpSensorExecutor.BerReader(content);
        var enterprise = SnmpSensorExecutor.DecodeObjectIdentifier(Expect(reader, 0x06, "enterprise").Content);
        var agentElement = reader.ReadElement();
        var agentAddress = agentElement.Content.Length == 4 ? new IPAddress(agentElement.Content).ToString() : null;
        var generic = SnmpSensorExecutor.DecodeSignedInteger(Expect(reader, 0x02, "generic-trap").Content);
        var specific = SnmpSensorExecutor.DecodeSignedInteger(Expect(reader, 0x02, "specific-trap").Content);
        var timestamp = reader.ReadElement();
        var uptime = TimeSpan.FromMilliseconds(SnmpSensorExecutor.DecodeSignedInteger(timestamp.Content) * 10.0);
        var varBinds = ReadVarBinds(reader);
        var trapOid = generic is >= 0 and <= 5
            ? GenericTrapPrefix + (generic + 1).ToString(CultureInfo.InvariantCulture)
            : $"{enterprise}.0.{specific.ToString(CultureInfo.InvariantCulture)}";
        return new SnmpTrap(1, community, SnmpTrapPduType.TrapV1, 0, trapOid, uptime, agentAddress, varBinds);
    }

    private static SnmpTrap ParseV2(string community, byte[] content, SnmpTrapPduType type)
    {
        var reader = new SnmpSensorExecutor.BerReader(content);
        var requestId = (int)SnmpSensorExecutor.DecodeSignedInteger(Expect(reader, 0x02, "request-id").Content);
        _ = reader.ReadElement(); // error-status
        _ = reader.ReadElement(); // error-index
        var all = ReadVarBinds(reader);
        TimeSpan? uptime = all.FirstOrDefault(binding => binding.Oid == SysUpTimeOid) is { Number: double seconds }
            ? TimeSpan.FromSeconds(seconds)
            : null;
        var trapOid = all.FirstOrDefault(binding => binding.Oid == SnmpTrapOidOid)?.Value ?? string.Empty;
        var agentAddress = all.FirstOrDefault(binding => binding.Oid == SnmpTrapAddressOid)?.Value;
        var varBinds = all.Where(binding => binding.Oid is not (SysUpTimeOid or SnmpTrapOidOid)).ToArray();
        return new SnmpTrap(2, community, type, requestId, trapOid, uptime, agentAddress, varBinds);
    }

    private static List<SnmpTrapVarBind> ReadVarBinds(SnmpSensorExecutor.BerReader reader)
    {
        var list = Expect(reader, 0x30, "varbind list");
        var bindings = new SnmpSensorExecutor.BerReader(list.Content);
        var result = new List<SnmpTrapVarBind>();
        while (bindings.TryReadElement(out var binding))
        {
            SnmpSensorExecutor.EnsureTag(binding.Tag, 0x30, "varbind");
            var item = new SnmpSensorExecutor.BerReader(binding.Content);
            var oid = SnmpSensorExecutor.DecodeObjectIdentifier(Expect(item, 0x06, "varbind OID").Content);
            var valueElement = item.ReadElement();
            var value = SnmpSensorExecutor.DecodeValue(valueElement.Tag, valueElement.Content);
            result.Add(new SnmpTrapVarBind(oid, value.Syntax, value.DisplayValue, value.NumericValue));
            if (result.Count >= 200)
            {
                break; // a trap with more bindings than this is not a trap anyone reads
            }
        }

        return result;
    }

    private static BerElementView Expect(SnmpSensorExecutor.BerReader reader, byte tag, string label)
    {
        var element = reader.ReadElement();
        SnmpSensorExecutor.EnsureTag(element.Tag, tag, label);
        return new BerElementView(element.Content);
    }

    private readonly record struct BerElementView(byte[] Content);

    /// <summary>The Response PDU an inform has to be answered with (same request id, same bindings), or the sender keeps
    /// repeating it.</summary>
    public static byte[] BuildInformResponse(SnmpTrap inform)
    {
        ArgumentNullException.ThrowIfNull(inform);
        var bindings = new List<byte>();
        if (inform.Uptime is TimeSpan uptime)
        {
            bindings.AddRange(VarBind(SysUpTimeOid, SnmpSensorExecutor.EncodeTagged(0x43, UnsignedBytes((long)(uptime.TotalMilliseconds / 10)))));
        }

        bindings.AddRange(VarBind(SnmpTrapOidOid, SnmpSensorExecutor.EncodeObjectIdentifier(inform.TrapOid)));
        var pdu = new List<byte>();
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(inform.RequestId));
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(0));
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(0));
        pdu.AddRange(SnmpSensorExecutor.EncodeSequence(bindings.ToArray()));
        return Message(inform.Community, SnmpSensorExecutor.EncodeTagged(0xA2, pdu.ToArray()));
    }

    /// <summary>An SNMPv2c trap - for the "Send a test trap" button and the tests. Values are sent as octet strings.</summary>
    public static byte[] BuildV2Trap(string community, string trapOid, TimeSpan uptime, IEnumerable<(string Oid, string Value)> values, int requestId = 1, bool inform = false)
    {
        var bindings = new List<byte>();
        bindings.AddRange(VarBind(SysUpTimeOid, SnmpSensorExecutor.EncodeTagged(0x43, UnsignedBytes((long)(uptime.TotalMilliseconds / 10)))));
        bindings.AddRange(VarBind(SnmpTrapOidOid, SnmpSensorExecutor.EncodeObjectIdentifier(trapOid)));
        foreach (var (oid, value) in values)
        {
            bindings.AddRange(VarBind(oid, SnmpSensorExecutor.EncodeOctetString(Encoding.UTF8.GetBytes(value))));
        }

        var pdu = new List<byte>();
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(requestId));
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(0));
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(0));
        pdu.AddRange(SnmpSensorExecutor.EncodeSequence(bindings.ToArray()));
        return Message(community, SnmpSensorExecutor.EncodeTagged(inform ? (byte)0xA6 : (byte)0xA7, pdu.ToArray()), version: 1);
    }

    /// <summary>An SNMPv1 trap - for the tests (devices still send them).</summary>
    public static byte[] BuildV1Trap(string community, string enterprise, string agentAddress, int generic, int specific, TimeSpan uptime, IEnumerable<(string Oid, long Value)> values)
    {
        var bindings = new List<byte>();
        foreach (var (oid, value) in values)
        {
            bindings.AddRange(VarBind(oid, SnmpSensorExecutor.EncodeInteger(value)));
        }

        var pdu = new List<byte>();
        pdu.AddRange(SnmpSensorExecutor.EncodeObjectIdentifier(enterprise));
        pdu.AddRange(SnmpSensorExecutor.EncodeTagged(0x40, IPAddress.Parse(agentAddress).GetAddressBytes()));
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(generic));
        pdu.AddRange(SnmpSensorExecutor.EncodeInteger(specific));
        pdu.AddRange(SnmpSensorExecutor.EncodeTagged(0x43, UnsignedBytes((long)(uptime.TotalMilliseconds / 10))));
        pdu.AddRange(SnmpSensorExecutor.EncodeSequence(bindings.ToArray()));
        return Message(community, SnmpSensorExecutor.EncodeTagged(0xA4, pdu.ToArray()), version: 0);
    }

    private static byte[] Message(string community, byte[] pdu, int version = 1)
    {
        var body = new List<byte>();
        body.AddRange(SnmpSensorExecutor.EncodeInteger(version));
        body.AddRange(SnmpSensorExecutor.EncodeOctetString(Encoding.UTF8.GetBytes(community)));
        body.AddRange(pdu);
        return SnmpSensorExecutor.EncodeSequence(body.ToArray());
    }

    private static byte[] VarBind(string oid, byte[] value)
    {
        var binding = new List<byte>();
        binding.AddRange(SnmpSensorExecutor.EncodeObjectIdentifier(oid));
        binding.AddRange(value);
        return SnmpSensorExecutor.EncodeSequence(binding.ToArray());
    }

    /// <summary>An unsigned value (TimeTicks, Counter) in the shortest big-endian form, with a leading 0 when the top bit
    /// is set - EncodeInteger's bytes without its INTEGER tag.</summary>
    private static byte[] UnsignedBytes(long value)
    {
        var encoded = SnmpSensorExecutor.EncodeInteger(Math.Max(0, value));
        var reader = new SnmpSensorExecutor.BerReader(encoded);
        return reader.ReadElement().Content;
    }
}
