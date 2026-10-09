using System.Net;
using Matmon.Core.Domain;

namespace Matmon.Host.Ui;

/// <summary>
/// The name an installation suggests for itself when it links to Matmon.Cloud. It used to be the root probe's name,
/// which on EVERY fresh install is the same "Primary Probe" - so under one account each new installation collided with
/// the previous one (the cloud used to reuse the old instance, license and all; it asks now, but "Primary Probe (2)",
/// "Primary Probe (3)" … still says nothing about which box is which).
///
/// In order of preference: a name the admin chose for the root probe (an explicit statement), the host name the admin
/// used to reach this instance (<c>nas</c> from <c>http://nas.fritz.box:8099</c> - something that differs from box to
/// box), the root probe's name, the machine name. An IP address or <c>localhost</c> says nothing about the machine, so
/// those fall through.
/// </summary>
public static class CloudInstanceNameSuggestion
{
    public static string Suggest(string? requestHost, string? rootProbeName, string machineName)
    {
        var probeName = rootProbeName?.Trim();
        if (!string.IsNullOrEmpty(probeName) && !string.Equals(probeName, ProbeElement.DefaultRootName, StringComparison.Ordinal))
        {
            return probeName;
        }

        return NameFromHost(requestHost) ?? (string.IsNullOrEmpty(probeName) ? machineName : probeName);
    }

    /// <summary>The first label of a host name, or null for an address, <c>localhost</c> or nothing.</summary>
    public static string? NameFromHost(string? host)
    {
        var trimmed = host?.Trim().Trim('[', ']').TrimEnd('.');
        if (string.IsNullOrEmpty(trimmed) || IPAddress.TryParse(trimmed, out _))
        {
            return null;
        }

        var label = trimmed.Split('.')[0];
        return label.Length is 0 or > 63 || string.Equals(label, "localhost", StringComparison.OrdinalIgnoreCase) ? null : label;
    }
}
