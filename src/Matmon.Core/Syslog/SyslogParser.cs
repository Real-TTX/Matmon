using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Matmon.Core.Syslog;

/// <summary>
/// Turns what arrived on the wire into a <see cref="SyslogMessage"/>. Real devices follow neither RFC faithfully, so
/// the rule is to keep the message and extract what can be extracted: RFC 5424 (versioned, structured), the BSD
/// format of RFC 3164 (what most switches send, with Cisco's sequence numbers, marks, milliseconds, time zone and
/// no-hostname variants) and rsyslog's ISO-timestamp variant of it. Anything else is kept as a raw message - a log
/// line that does not parse is still the line you will want after a reboot.
/// </summary>
public static partial class SyslogParser
{
    /// <summary>Longer messages are cut. Real syslog messages are a few hundred bytes; the cap protects the store
    /// from a sender that is not really syslog.</summary>
    public const int MaxMessageLength = 8192;

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>UTF-8, falling back to Latin-1 for the old devices that send 8-bit text.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static SyslogMessage? Parse(ReadOnlySpan<byte> bytes, DateTimeOffset receivedUtc, string sourceAddress) =>
        Parse(Decode(bytes), receivedUtc, sourceAddress);

    /// <summary>The parsed message, or null when there is nothing in it (an empty datagram).</summary>
    public static SyslogMessage? Parse(string? text, DateTimeOffset receivedUtc, string sourceAddress)
    {
        if (text is null)
        {
            return null;
        }

        var line = CleanControlCharacters(text.TrimStart('﻿').TrimEnd('\r', '\n', '\0', ' ', '\t'));
        if (line.Length == 0)
        {
            return null;
        }

        var facility = 1;
        var severity = SyslogSeverity.Notice;
        var hasPriority = false;
        var body = line;
        if (TryReadPriority(line, out var priority, out var afterPriority))
        {
            facility = priority >> 3;
            severity = priority & 7;
            hasPriority = true;
            body = line[afterPriority..];
        }

        var parts = Rfc5424Start().IsMatch(body) ? ParseRfc5424(body) : ParseBsd(body, hasPriority);

        var message = parts.Message.Length > MaxMessageLength
            ? parts.Message[..MaxMessageLength] + "…"
            : parts.Message;

        return new SyslogMessage
        {
            ReceivedUtc = receivedUtc,
            SourceAddress = sourceAddress.Trim(),
            Facility = facility,
            Severity = severity,
            DeviceTime = parts.DeviceTime,
            Hostname = parts.Hostname,
            AppName = parts.AppName,
            ProcId = parts.ProcId,
            MsgId = parts.MsgId,
            Message = message,
            Format = parts.Format
        };
    }

    private readonly record struct Parts(
        string? DeviceTime,
        string? Hostname,
        string? AppName,
        string? ProcId,
        string? MsgId,
        string Message,
        SyslogFormat Format);

    // ---- <PRI> ---------------------------------------------------------------------------------------------------

    private static bool TryReadPriority(string line, out int priority, out int next)
    {
        priority = 0;
        next = 0;
        if (line.Length < 3 || line[0] != '<')
        {
            return false;
        }

        var close = line.IndexOf('>', 1);
        if (close is < 2 or > 4)
        {
            return false;
        }

        // NumberStyles.None: digits only - no sign, no spaces - and 191 = facility 23 * 8 + severity 7 is the top.
        if (!int.TryParse(line.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out priority) || priority > 191)
        {
            return false;
        }

        next = close + 1;
        return true;
    }

    // ---- RFC 5424 ------------------------------------------------------------------------------------------------

    // "1 2026-10-15T10:11:12Z host app proc msgid ..." - a version, then a timestamp (or the NILVALUE "-") and a space.
    [GeneratedRegex(@"^[1-9]\d? (?=(?:-|\d{4}-\d{2}-\d{2}T)\S* )")]
    private static partial Regex Rfc5424Start();

    private static Parts ParseRfc5424(string body)
    {
        var index = body.IndexOf(' ') + 1;
        var timestamp = ReadToken(body, ref index);
        var hostname = NilToNull(ReadToken(body, ref index));
        var appName = NilToNull(ReadToken(body, ref index));
        var procId = NilToNull(ReadToken(body, ref index));
        var msgId = NilToNull(ReadToken(body, ref index));
        var structuredData = ReadStructuredData(body, ref index);

        var text = index < body.Length ? body[index..] : string.Empty;
        text = text.TrimStart('﻿'); // the UTF-8 byte order mark an RFC 5424 sender puts in front of the text

        // Structured data is where some devices (Juniper, Windows loggers) put the substance, so it stays with the text.
        var message = structuredData is { Length: > 0 }
            ? (text.Length > 0 ? structuredData + " " + text : structuredData)
            : text;

        return new Parts(NilToNull(timestamp), hostname, appName, procId, msgId, message, SyslogFormat.Rfc5424);
    }

    private static string ReadToken(string text, ref int index)
    {
        if (index >= text.Length)
        {
            return "-";
        }

        var end = text.IndexOf(' ', index);
        if (end < 0)
        {
            var rest = text[index..];
            index = text.Length;
            return rest;
        }

        var token = text[index..end];
        index = end + 1;
        return token;
    }

    private static string? NilToNull(string token) => token is "-" or "" ? null : token;

    /// <summary>"-" or one or more <c>[id key="value" ...]</c> elements, where <c>\"</c>, <c>\\</c> and <c>\]</c> are
    /// escapes. Leaves <paramref name="index"/> on the first character of the text.</summary>
    private static string? ReadStructuredData(string text, ref int index)
    {
        if (index >= text.Length)
        {
            return null;
        }

        if (text[index] == '-')
        {
            index++;
            if (index < text.Length && text[index] == ' ')
            {
                index++;
            }

            return null;
        }

        if (text[index] != '[')
        {
            return null; // not structured data at all: what follows is the message
        }

        var start = index;
        while (index < text.Length && text[index] == '[')
        {
            index++;
            while (index < text.Length)
            {
                if (text[index] == '\\' && index + 1 < text.Length)
                {
                    index += 2;
                }
                else if (text[index] == ']')
                {
                    break;
                }
                else
                {
                    index++;
                }
            }

            if (index < text.Length)
            {
                index++; // the closing ]
            }
        }

        var data = text[start..index];
        if (index < text.Length && text[index] == ' ')
        {
            index++;
        }

        return data;
    }

    // ---- BSD syslog (RFC 3164) and the dialects that look like it ---------------------------------------------------

    // Optional Cisco prefixes ("sw1: ", the sequence number "000123: ", the clock marker "*" or "."), then
    // "Mmm [d]d [yyyy ]hh:mm:ss[.mmm]" with an optional upper-case zone that must be followed by a colon (otherwise
    // it would eat a host name), and the colon Cisco puts after the timestamp.
    [GeneratedRegex(
        @"^(?:(?<origin>(?!\d+: )[A-Za-z0-9_.\-]+): )?(?:(?<seq>\d+): )?[*.]?(?<ts>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) {1,2}\d{1,2}(?: \d{4})? \d{2}:\d{2}:\d{2}(?:\.\d{1,6})?)(?: (?<tz>[A-Z]{2,5}|[+-]\d{2}:?\d{2})(?=:))?(?<colon>:)? *")]
    private static partial Regex BsdPrefix();

    // rsyslog's high-precision format: "2026-10-15T10:11:12.123456+02:00 host tag: message".
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:Z|[+-]\d{2}:?\d{2})?) +")]
    private static partial Regex IsoPrefix();

    [GeneratedRegex(@"^(?<app>[^\s\[\]:]+)(?:\[(?<pid>[^\]]*)\])?:$")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,254}$")]
    private static partial Regex HostnameShape();

    private static Parts ParseBsd(string body, bool hasPriority)
    {
        string? deviceTime = null;
        string? origin = null;
        var rest = body;
        var headerEndedAtColon = false;
        var hadTimestamp = false;

        var bsd = BsdPrefix().Match(body);
        if (bsd.Success)
        {
            hadTimestamp = true;
            deviceTime = bsd.Groups["tz"].Success ? $"{bsd.Groups["ts"].Value} {bsd.Groups["tz"].Value}" : bsd.Groups["ts"].Value;
            origin = bsd.Groups["origin"].Success ? bsd.Groups["origin"].Value : null;
            headerEndedAtColon = bsd.Groups["colon"].Success;
            rest = body[bsd.Length..];
        }
        else if (IsoPrefix().Match(body) is { Success: true } iso)
        {
            hadTimestamp = true;
            deviceTime = iso.Groups["ts"].Value;
            rest = body[iso.Length..];
        }

        string? hostname = origin;
        string? appName = null;
        string? procId = null;
        string? msgId = null;
        string message;

        rest = rest.TrimStart(' ');
        if (rest.Length == 0)
        {
            message = string.Empty;
        }
        else if (rest[0] == '%')
        {
            // Cisco: "%LINK-3-UPDOWN: Interface ..." - the mnemonic is the identity of the message, and it stays in the text
            // because it is also what you search for.
            msgId = ReadMnemonic(rest);
            message = rest;
        }
        else
        {
            var (first, afterFirst) = SplitFirst(rest);
            if (TryReadTag(first, out appName, out procId))
            {
                message = afterFirst;
            }
            else if (!hadTimestamp || headerEndedAtColon || afterFirst.Length == 0 || !HostnameShape().IsMatch(first))
            {
                // No timestamp means nothing to anchor a host name on; a colon right after the timestamp is Cisco saying
                // "the header ends here"; and a first word that is not shaped like a host name is just the text.
                message = rest;
            }
            else
            {
                hostname = first;
                var (second, afterSecond) = SplitFirst(afterFirst);
                if (second.StartsWith('%'))
                {
                    msgId = ReadMnemonic(second);
                    message = afterFirst;
                }
                else if (TryReadTag(second, out appName, out procId))
                {
                    message = afterSecond;
                }
                else
                {
                    message = afterFirst;
                }
            }
        }

        var format = hasPriority || hadTimestamp || appName is not null || msgId is not null
            ? SyslogFormat.Rfc3164
            : SyslogFormat.Raw;
        return new Parts(deviceTime, hostname, appName, procId, msgId, message, format);
    }

    private static (string First, string Remainder) SplitFirst(string text)
    {
        var end = text.IndexOf(' ');
        return end < 0 ? (text, string.Empty) : (text[..end], text[(end + 1)..].TrimStart(' '));
    }

    private static bool TryReadTag(string token, out string? appName, out string? procId)
    {
        appName = null;
        procId = null;
        var match = Tag().Match(token);
        if (!match.Success)
        {
            return false;
        }

        appName = match.Groups["app"].Value;
        procId = match.Groups["pid"].Success && match.Groups["pid"].Length > 0 ? match.Groups["pid"].Value : null;
        return true;
    }

    /// <summary><c>%LINK-3-UPDOWN:</c> becomes <c>LINK-3-UPDOWN</c>.</summary>
    private static string? ReadMnemonic(string text)
    {
        var end = 1;
        while (end < text.Length && text[end] != ':' && text[end] != ' ')
        {
            end++;
        }

        return end > 1 ? text[1..end] : null;
    }

    /// <summary>NUL and the other control characters have no business in a log line (and a NUL would cut the message
    /// off in some databases); new lines and tabs stay, since a framed TCP message can really contain them.</summary>
    private static string CleanControlCharacters(string text)
    {
        var needsCleaning = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not '\n' and not '\t' and not '\r')
            {
                needsCleaning = true;
                break;
            }
        }

        if (!needsCleaning)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsControl(character) && character is not '\n' and not '\t' and not '\r' ? ' ' : character);
        }

        return builder.ToString();
    }
}
