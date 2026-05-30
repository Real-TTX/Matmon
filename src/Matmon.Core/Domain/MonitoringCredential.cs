using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

public enum MonitoringCredentialKind
{
    Generic = 0,
    Linux = 1,
    Ssh = 2,
    Windows = 3,
    Proxmox = 4,
    Snmp = 5,
    SqlServer = 6
}

public sealed class MonitoringCredentialBundle
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public MonitoringCredentialKind Kind { get; set; } = MonitoringCredentialKind.Generic;

    public string? Description { get; set; }

    public string ProtectedValues { get; set; } = string.Empty;

    [JsonIgnore]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public MonitoringCredentialBundle Clone()
    {
        return new MonitoringCredentialBundle
        {
            Id = Id,
            Name = Name,
            Kind = Kind,
            Description = Description,
            ProtectedValues = ProtectedValues,
            Values = new Dictionary<string, string>(Values, StringComparer.OrdinalIgnoreCase)
        };
    }

    public bool ContentEquals(MonitoringCredentialBundle other)
    {
        if (Id != other.Id ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
            Kind != other.Kind ||
            !string.Equals(Description, other.Description, StringComparison.Ordinal))
        {
            return false;
        }

        if (Values.Count != other.Values.Count)
        {
            return false;
        }

        foreach (var pair in Values)
        {
            if (!other.Values.TryGetValue(pair.Key, out var otherValue) ||
                !string.Equals(pair.Value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
