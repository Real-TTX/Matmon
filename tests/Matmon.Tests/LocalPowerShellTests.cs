using Matmon.Core.Domain;

namespace Matmon.Tests;

// On a Windows probe - an agent above all - a Windows sensor whose target is the machine itself runs its script
// locally: no WinRM listener, no credential. The live tests only mean something on Windows and are no-ops
// elsewhere (the Linux CI container has no powershell.exe to run).
public sealed class LocalPowerShellTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("localhost", true)]
    [InlineData(".", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("nas.example.local", false)]
    [InlineData("10.0.0.5", false)]
    public void WhatCountsAsThisMachine(string? target, bool expected)
    {
        Assert.Equal(expected, PowerShellRemoteSensorExecutor.IsLocalTarget(target));
    }

    [Fact]
    public void TheMachinesOwnNameCountsBareAndAsFqdn()
    {
        Assert.True(PowerShellRemoteSensorExecutor.IsLocalTarget(Environment.MachineName.ToLowerInvariant()));
        Assert.True(PowerShellRemoteSensorExecutor.IsLocalTarget(Environment.MachineName + ".corp.example"));
        Assert.False(PowerShellRemoteSensorExecutor.IsLocalTarget(Environment.MachineName + "-2"));
    }

    [Fact]
    public void One_of_the_machines_own_addresses_counts_as_this_machine()
    {
        // An agent bound to a host that was added by its address measures that address: it must run locally, not go
        // out over WinRM to itself (which failed on a Windows box without PowerShell 7).
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address));
        if (own is null)
        {
            return; // no IPv4 interface on this machine
        }

        Assert.True(PowerShellRemoteSensorExecutor.IsLocalTarget(own.ToString()));
        Assert.False(PowerShellRemoteSensorExecutor.IsLocalTarget("203.0.113.77")); // documentation range, never ours
    }

    [Fact]
    public async Task The_winrm_wrapper_runs_in_windows_powershell_5()
    {
        // Without PowerShell 7 the WinRM session is opened by Windows PowerShell 5.1, which has no "??" operator: the
        // wrapper has to parse there. No password = it stops at its own first check, which proves it got that far.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["MATMON_PS_SCRIPT_B64"] = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("1"));
        startInfo.Environment["MATMON_PASSWORD"] = string.Empty;
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(PowerShellRemoteSensorExecutor.BuildPowerShellRemotingWrapperCommand())) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Contains("Windows password is required", error);
        Assert.DoesNotContain("Unexpected token", error);
    }

    [Fact]
    public void Windows_always_has_a_shell_for_winrm()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(File.Exists(PowerShellRemoteSensorExecutor.RemotingShell()));
    }

    [Fact]
    public async Task AScriptRunsLocallyWithoutAnyCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(60) };
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["defaultChannelKey"] = "answer";
        settings.Parameters["script"] = "[pscustomobject]@{ answer = 42; host = $env:COMPUTERNAME.Length; umlaut = 'Größe' }";

        var result = await new PowerShellRemoteSensorExecutor().ExecuteAsync(new SensorExecutionContext("powershell", "localhost", settings));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(42, result.Value);
        Assert.Contains(result.Channels, channel => channel.Key == "host" && channel.Value > 0);
    }

    [Fact]
    public async Task AWindowsServiceIsCheckedLocally()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // WMI runs on every Windows machine.
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(60) };
        settings.Parameters["windows.serviceName"] = "Winmgmt";
        settings.Parameters["windows.serviceExpectedState"] = "Running";

        var result = await new WindowsServiceSensorExecutor().ExecuteAsync(new SensorExecutionContext("windows-service", string.Empty, settings));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Contains(result.Channels, channel => string.Equals(channel.Key, "stateOk", StringComparison.OrdinalIgnoreCase) && channel.Value == 1);
    }

    [Fact]
    public async Task AFailingScriptIsReportedNotSwallowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(60) };
        settings.Parameters["outputFormat"] = "json";
        settings.Parameters["script"] = "throw 'disk on fire'";

        var result = await new PowerShellRemoteSensorExecutor().ExecuteAsync(new SensorExecutionContext("powershell", ".", settings));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("disk on fire", result.Message);
    }
}
