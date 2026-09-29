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
