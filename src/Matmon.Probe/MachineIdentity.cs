using System.Diagnostics;
using Matmon.Core.Domain;

namespace Matmon.Probe;

/// <summary>
/// The machine's own id, hashed (<see cref="AgentMachineId.FromRaw"/>) - what lets the instance recognise a machine that
/// enrols again (a reinstall, the MSI rolled out once more) instead of putting a second host for it into the tree.
/// Windows: the MachineGuid Windows creates at setup; Linux: /etc/machine-id; macOS: the hardware's IOPlatformUUID.
/// Null when none can be read - enrolment then simply works without it.
/// </summary>
public static class MachineIdentity
{
    public static string? Read()
    {
        try
        {
            return AgentMachineId.FromRaw(ReadRaw());
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRaw()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id)
                {
                    return id;
                }
            }

            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var line = output.Split('\n').FirstOrDefault(candidate => candidate.Contains("IOPlatformUUID", StringComparison.Ordinal));
            var parts = line?.Split('"');
            return parts is { Length: >= 4 } ? parts[3] : null;
        }

        return null;
    }
}
