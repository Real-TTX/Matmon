namespace Matmon.Core.Domain;

/// <summary>
/// "This machine" as a sensor target - the one rule both remote engines (PowerShell/WinRM and SSH) use to
/// decide that a script should simply run locally, which is what an agent monitoring the box it sits on wants:
/// no listener, no credential. No target, localhost / loopback / ".", or the machine's own name (bare or as the
/// first label of an FQDN). Anything else - including another host's IP - stays remote.
/// </summary>
public static class LocalTarget
{
    public static bool Is(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        var host = target.Trim();
        if (host is "." or "localhost" or "127.0.0.1" or "::1" or "[::1]")
        {
            return true;
        }

        var machine = Environment.MachineName;
        return string.Equals(host, machine, StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase);
    }
}
