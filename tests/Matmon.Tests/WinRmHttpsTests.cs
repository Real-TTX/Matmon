using Matmon.Core.Domain;

namespace Matmon.Tests;

// WinRM over HTTPS with a self-signed certificate: the switch that skips the certificate check reaches every WinRM sensor,
// is only offered while "Use SSL" is on, gets into the remoting wrapper, and a refused certificate says what to do.
public sealed class WinRmHttpsTests
{
    [Fact]
    public void Every_winrm_sensor_offers_the_switch_only_with_ssl()
    {
        foreach (var definition in new[]
                 {
                     PowerShellRemoteSensorExecutor.Definition, WindowsHealthSensorExecutor.Definition, WindowsDiskSensorExecutor.Definition,
                     WindowsEventLogSensorExecutor.Definition, WindowsServiceSensorExecutor.Definition, WindowsProcessSensorExecutor.Definition
                 })
        {
            var parameter = Assert.Single(definition.Parameters, p => p.Key == PowerShellRemoteSensorExecutor.SkipCertificateCheckKey);
            Assert.Equal("false", parameter.DefaultValue);
            Assert.Equal("winrm.useSsl", parameter.VisibleWhenParameterKey);
            Assert.Equal(["true"], parameter.VisibleWhenValues);
        }
    }

    [Fact]
    public void The_wrapper_skips_the_checks_only_when_asked()
    {
        var wrapper = PowerShellRemoteSensorExecutor.BuildPowerShellRemotingWrapperCommand();
        Assert.Contains("MATMON_SKIPCERTCHECK", wrapper);
        Assert.Contains("New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck", wrapper);
    }

    [Fact]
    public void A_refused_certificate_points_at_the_switch()
    {
        const string error = "Connecting to remote server nas failed: The SSL certificate is signed by an unknown certificate authority.";
        var hint = PowerShellRemoteSensorExecutor.BuildRemoteFailureHint(error, "nas", 5986, useSsl: true);
        Assert.Contains("Do not check the certificate", hint);

        // already switched on, or plain HTTP: the normal message
        Assert.DoesNotContain("Do not check the certificate", PowerShellRemoteSensorExecutor.BuildRemoteFailureHint(error, "nas", 5986, useSsl: true, skipCertificateCheck: true));
        Assert.DoesNotContain("Do not check the certificate", PowerShellRemoteSensorExecutor.BuildRemoteFailureHint("Access is denied", "nas", 5985, useSsl: false));
    }
}
