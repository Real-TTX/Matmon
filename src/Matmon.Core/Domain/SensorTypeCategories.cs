namespace Matmon.Core.Domain;

/// <summary>
/// Groups sensor types into UI categories for the sensor-type dropdown (rendered as
/// <c>&lt;optgroup&gt;</c>s). Pure key → category-name mapping; turning these into
/// <c>SelectListItem</c> groups lives in the Host. Unmapped keys fall into <see cref="Other"/>.
/// </summary>
public static class SensorTypeCategories
{
    public const string Other = "Other";

    /// <summary>Categories in the order they should appear in the dropdown.</summary>
    public static readonly IReadOnlyList<string> Order =
    [
        "Network",
        "Certificates",
        "Windows",
        "Linux",
        "Scripting",
        "Virtualization & NAS",
        "Storage",
        "Databases",
        "Probe",
        Other
    ];

    private static readonly IReadOnlyDictionary<string, string> ByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ping"] = "Network",
        ["http"] = "Network",
        ["http-advanced"] = "Network",
        ["tcp-port"] = "Network",
        ["dns"] = "Network",
        ["ntp"] = "Network",
        ["snmp"] = "Network",
        ["snmp-interface"] = "Network",
        ["ups-snmp"] = "Network",

        ["ssl-certificate"] = "Certificates",
        ["certificate-chain"] = "Certificates",

        ["windows-service"] = "Windows",
        ["windows-process"] = "Windows",
        ["windows-disk"] = "Windows",

        ["linux-ssh-health"] = "Linux",
        ["linux-disk"] = "Linux",

        ["powershell"] = "Scripting",
        ["local-script"] = "Scripting",
        ["local-program"] = "Scripting",

        ["proxmox"] = "Virtualization & NAS",
        ["proxmox-disk"] = "Virtualization & NAS",
        ["synology"] = "Virtualization & NAS",
        ["synology-health"] = "Virtualization & NAS",
        ["synology-disk"] = "Virtualization & NAS",
        ["unifi-health"] = "Virtualization & NAS",
        ["docker-container"] = "Virtualization & NAS",

        ["disk-smart"] = "Storage",
        ["backup-job"] = "Storage",

        ["mssql"] = "Databases",

        ["probe-heartbeat"] = "Probe",
        ["probe-health"] = "Probe",
    };

    /// <summary>The category a sensor type belongs to, or <see cref="Other"/>.</summary>
    public static string Resolve(string? typeKey) =>
        !string.IsNullOrWhiteSpace(typeKey) && ByKey.TryGetValue(typeKey.Trim(), out var category)
            ? category
            : Other;

    /// <summary>Sort index of a category in <see cref="Order"/> (unknown sorts last).</summary>
    public static int OrderIndex(string category)
    {
        for (var index = 0; index < Order.Count; index++)
        {
            if (string.Equals(Order[index], category, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return Order.Count;
    }
}
