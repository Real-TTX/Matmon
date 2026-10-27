using System.Globalization;
using System.Xml.Linq;

namespace Matmon.Core.Import;

public enum PrtgNodeKind { Probe, Group, Device, Sensor }

/// <summary>One node of a PRTG sensor tree (<c>/api/table.xml?content=sensortree</c>): a probe, a group, a device or a sensor.</summary>
public sealed record PrtgNode(
    PrtgNodeKind Kind,
    int Id,
    string Name,
    string? Host,
    string? SensorKind,
    string? SensorType,
    int? IntervalSeconds,
    bool Paused,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PrtgNode> Children)
{
    public IEnumerable<PrtgNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var below in child.Descendants())
            {
                yield return below;
            }
        }
    }
}

/// <summary>
/// Reads PRTG's sensor tree (XML of <c>table.xml?content=sensortree</c>): probes (<c>probenode</c>), groups, devices (with
/// <c>host</c>) and sensors (with <c>sensorkind</c> - PRTG's internal type id - <c>sensortype</c>, <c>interval</c>, the
/// paused state and <c>tags</c>). The root group itself is not a node - its probes are the top level. Tolerant: an element
/// it does not know is skipped, a missing field is null.
/// </summary>
public static class PrtgSensorTree
{
    public static IReadOnlyList<PrtgNode> Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var nodes = document.Root?.Element("sensortree")?.Element("nodes") ?? document.Root;
        if (nodes is null)
        {
            return [];
        }
        // the tree starts with the root group (id 0): its probes are what matters
        var root = nodes.Elements("group").FirstOrDefault();
        var top = root is not null && root.Elements("probenode").Any() ? root : nodes;
        return ReadChildren(top);
    }

    private static List<PrtgNode> ReadChildren(XElement parent)
    {
        var result = new List<PrtgNode>();
        foreach (var element in parent.Elements())
        {
            var kind = element.Name.LocalName switch
            {
                "probenode" => PrtgNodeKind.Probe,
                "group" => PrtgNodeKind.Group,
                "device" => PrtgNodeKind.Device,
                "sensor" => PrtgNodeKind.Sensor,
                _ => (PrtgNodeKind?)null
            };
            if (kind is null)
            {
                continue;
            }
            result.Add(new PrtgNode(
                kind.Value,
                int.TryParse((string?)element.Attribute("id") ?? (string?)element.Element("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0,
                Text(element, "name") ?? $"{kind} {id}",
                kind == PrtgNodeKind.Device ? Text(element, "host") : null,
                kind == PrtgNodeKind.Sensor ? Text(element, "sensorkind") : null,
                kind == PrtgNodeKind.Sensor ? Text(element, "sensortype") : null,
                Interval(Text(element, "interval")),
                IsPaused(element),
                (Text(element, "tags") ?? string.Empty).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                kind == PrtgNodeKind.Sensor ? [] : ReadChildren(element)));
        }
        return result;
    }

    private static string? Text(XElement element, string name)
    {
        var value = element.Element(name)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>PRTG writes the interval as seconds ("60"), sometimes with text after it ("60 seconds", "60|60 seconds").</summary>
    private static int? Interval(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;
    }

    /// <summary>Paused by the user or by a dependency/schedule: <c>active</c> 0, or a status / raw status of a paused state (7-12).</summary>
    private static bool IsPaused(XElement element)
    {
        var active = (string?)element.Element("active");
        if (active is "0" or "false")
        {
            return true;
        }
        var raw = (string?)element.Element("status_raw");
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code is >= 7 and <= 12)
        {
            return true;
        }
        return ((string?)element.Element("status"))?.Contains("paus", StringComparison.OrdinalIgnoreCase) == true;
    }
}

public enum PrtgMappingLevel
{
    /// <summary>Taken over completely: the Matmon sensor checks the same thing with what the tree and the sensor's properties say.</summary>
    Supported,
    /// <summary>Created, but something must be added in Matmon before it works - credentials (PRTG's API never hands them out) or a setting PRTG did not report.</summary>
    Partial,
    /// <summary>Matmon checks this itself (PRTG's own probe / core health) - not imported.</summary>
    Covered,
    /// <summary>No Matmon sensor checks it the same way - listed, not imported.</summary>
    Unsupported
}

/// <summary>What a PRTG sensor becomes: the Matmon sensor type, how complete it is, which of the PRTG sensor's properties
/// to read (<c>getobjectproperty</c>) and what is still missing.</summary>
public sealed record PrtgMapping(PrtgMappingLevel Level, string? SensorTypeKey, IReadOnlyList<string> Properties, string Note);

/// <summary>
/// PRTG sensor type -> Matmon sensor type. Matched on PRTG's internal <c>sensorkind</c> (and, where that is not
/// enough, the display type), by prefix, so a newer variant of a type ("dnsv2", "httpadvanced") lands on the same Matmon
/// sensor. Adding a type = one rule here and one case in <see cref="Settings"/>. Not verified against a real PRTG: the
/// kinds and property names come from PRTG's documentation and API.
/// </summary>
public static class PrtgSensorMapping
{
    private static readonly IReadOnlyList<string> None = [];

    public static PrtgMapping Map(string? sensorKind, string? sensorType)
    {
        var kind = (sensorKind ?? string.Empty).Trim().ToLowerInvariant();
        var type = (sensorType ?? string.Empty).Trim().ToLowerInvariant();
        bool Is(params string[] prefixes) => prefixes.Any(prefix => kind.StartsWith(prefix, StringComparison.Ordinal));

        if (Is("probehealth", "corehealth", "systemhealth", "clusterhealth", "corestate"))
        {
            return new(PrtgMappingLevel.Covered, null, None, "PRTG's own health - Matmon watches its probes itself.");
        }
        if (Is("ping"))
        {
            return new(PrtgMappingLevel.Supported, "ping", None, "Ping with packet loss and jitter.");
        }
        if (Is("httpadvanced", "httptransaction", "http"))
        {
            return new(PrtgMappingLevel.Supported, "http", ["httpurl"], "HTTP check of the sensor's URL.");
        }
        if (Is("sslcertificate", "sslcert"))
        {
            return new(PrtgMappingLevel.Supported, "ssl-certificate", ["port"], "Certificate expiry of the device.");
        }
        if (Is("port"))
        {
            return new(PrtgMappingLevel.Supported, "tcp-port", ["port"], "TCP port check.");
        }
        if (Is("ntp", "sntp"))
        {
            return new(PrtgMappingLevel.Supported, "ntp", None, "NTP server check.");
        }
        if (Is("dns"))
        {
            return new(PrtgMappingLevel.Partial, "dns", ["dnsname", "domain"], "DNS lookup - check the name it resolves.");
        }
        if (Is("snmptraffic"))
        {
            return new(PrtgMappingLevel.Partial, "snmp-interface", ["interfacenumber"], "Interface traffic - needs the SNMP credential (and the interface index if PRTG does not report it).");
        }
        if (Is("wmiservice", "winservice"))
        {
            return new(PrtgMappingLevel.Partial, "windows-service", ["service", "servicename"], "Windows service - needs the Windows credential.");
        }
        if (Is("wmiprocess", "winprocess"))
        {
            return new(PrtgMappingLevel.Partial, "windows-process", ["process", "processname"], "Windows process - needs the Windows credential.");
        }
        if (Is("wmidisk", "wmivolume", "wmilogicaldisk"))
        {
            return new(PrtgMappingLevel.Partial, "windows-disk", None, "Windows disks - needs the Windows credential.");
        }
        if (Is("wmi", "windows", "perfcounter"))
        {
            return new(PrtgMappingLevel.Partial, "windows-health", None, "Windows health (CPU, memory, disk) - needs the Windows credential.");
        }
        if (Is("sshdisk"))
        {
            return new(PrtgMappingLevel.Partial, "linux-disk", None, "Linux disks over SSH - needs the SSH credential.");
        }
        if (Is("ssh"))
        {
            return new(PrtgMappingLevel.Partial, "linux-ssh-health", None, "Linux health over SSH - needs the SSH credential.");
        }
        return new(PrtgMappingLevel.Unsupported, null, None, string.IsNullOrEmpty(type) && string.IsNullOrEmpty(kind)
            ? "PRTG did not say what kind of sensor this is."
            : $"No Matmon sensor checks \"{sensorType ?? sensorKind}\" the same way.");
    }

    /// <summary>The target and the parameters of the Matmon sensor, from the PRTG sensor's properties (name -> value; a
    /// property PRTG did not answer is absent). An empty target = the host's address.</summary>
    public static (string Target, Dictionary<string, string> Parameters, string? Missing) Settings(string sensorTypeKey, IReadOnlyDictionary<string, string> properties, string? deviceHost)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? First(params string[] names) => names.Select(name => properties.TryGetValue(name, out var value) ? value?.Trim() : null).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        switch (sensorTypeKey)
        {
            case "http":
            {
                var url = First("httpurl");
                if (url is null)
                {
                    return ($"http://{deviceHost}", parameters, "PRTG did not report the URL - the device's address is used.");
                }
                // a PRTG URL may be relative to the device ("/status"): put the device in front
                if (url.StartsWith('/') && !string.IsNullOrEmpty(deviceHost))
                {
                    url = $"http://{deviceHost}{url}";
                }
                return (url, parameters, null);
            }
            case "tcp-port":
            {
                if (First("port") is { } port && int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is > 0 and < 65536)
                {
                    parameters["tcp.port"] = number.ToString(CultureInfo.InvariantCulture);
                    return (string.Empty, parameters, null);
                }
                return (string.Empty, parameters, "PRTG did not report the port - set it on the sensor.");
            }
            case "ssl-certificate":
                if (First("port") is { } sslPort && int.TryParse(sslPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sslNumber) && sslNumber is > 0 and < 65536)
                {
                    parameters["ssl.port"] = sslNumber.ToString(CultureInfo.InvariantCulture);
                }
                return (string.Empty, parameters, null);
            case "dns":
                if (First("dnsname", "domain") is { } name)
                {
                    parameters["dns.recordName"] = name;
                    return (string.Empty, parameters, null);
                }
                return (string.Empty, parameters, "PRTG did not report the name to resolve - set it on the sensor.");
            case "snmp-interface":
                if (First("interfacenumber") is { } index && int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifIndex) && ifIndex > 0)
                {
                    parameters["snmp.interfaceIndex"] = ifIndex.ToString(CultureInfo.InvariantCulture);
                    return (string.Empty, parameters, "Add the SNMP credential.");
                }
                return (string.Empty, parameters, "Add the SNMP credential and the interface index.");
            case "windows-service":
                if (First("service", "servicename") is { } service)
                {
                    parameters["windows.serviceName"] = service;
                    return (string.Empty, parameters, "Add the Windows credential.");
                }
                return (string.Empty, parameters, "Add the Windows credential and the service name.");
            case "windows-process":
                if (First("process", "processname") is { } process)
                {
                    parameters["windows.processName"] = process;
                }
                return (string.Empty, parameters, "Add the Windows credential.");
            case "windows-health" or "windows-disk":
                return (string.Empty, parameters, "Add the Windows credential.");
            case "linux-ssh-health" or "linux-disk":
                return (string.Empty, parameters, "Add the SSH credential.");
            default:
                return (string.Empty, parameters, null);
        }
    }
}
