using Matmon.Core.Domain;

namespace Matmon.Tests;

// A Generic credential bundle should serve sensors that would normally need a typed bundle,
// as a fallback when no kind-specific bundle exists (its username/password/token mapped onto
// the keys each sensor actually reads).
public class CredentialFallbackTests
{
    private static MonitoringSettings WithGeneric(string user, string pass, string? token = null)
    {
        var settings = new MonitoringSettings();
        var bundle = new MonitoringCredentialBundle { Name = "Shared", Kind = MonitoringCredentialKind.Generic };
        bundle.Values["generic.username"] = user;
        bundle.Values["generic.password"] = pass;
        if (token is not null)
        {
            bundle.Values["generic.token"] = token;
        }

        settings.Credentials.Add(bundle);
        return settings;
    }

    [Fact]
    public void Generic_bundle_serves_a_windows_sensor_when_no_windows_bundle()
    {
        var settings = WithGeneric("admin", "secret");

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, [MonitoringCredentialKind.Windows]);

        Assert.Equal("admin", settings.Parameters["winrm.username"]);
        Assert.Equal("secret", settings.Parameters["winrm.password"]);
    }

    [Fact]
    public void Generic_bundle_maps_to_ssh_for_an_ssh_or_linux_sensor()
    {
        var settings = WithGeneric("root", "pw");

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, [MonitoringCredentialKind.Linux]);

        Assert.Equal("root", settings.Parameters["ssh.username"]);
        Assert.Equal("pw", settings.Parameters["ssh.password"]);
    }

    [Fact]
    public void A_kind_specific_bundle_wins_over_the_generic_fallback()
    {
        var settings = WithGeneric("generic-user", "generic-pw");
        var windows = new MonitoringCredentialBundle { Name = "Win", Kind = MonitoringCredentialKind.Windows };
        windows.Values["winrm.username"] = "win-user";
        windows.Values["winrm.password"] = "win-pw";
        settings.Credentials.Add(windows);

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, [MonitoringCredentialKind.Windows]);

        Assert.Equal("win-user", settings.Parameters["winrm.username"]);
        Assert.Equal("win-pw", settings.Parameters["winrm.password"]);
    }

    [Fact]
    public void No_generic_bundle_means_no_fallback_values()
    {
        var settings = new MonitoringSettings();

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, [MonitoringCredentialKind.Windows]);

        Assert.False(settings.Parameters.ContainsKey("winrm.username"));
    }

    [Fact]
    public void An_explicitly_selected_generic_bundle_serves_a_typed_sensor()
    {
        var settings = WithGeneric("admin", "secret");
        settings.SelectedCredentialId = settings.Credentials[0].Id;

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, [MonitoringCredentialKind.SqlServer]);

        Assert.Equal("admin", settings.Parameters["mssql.username"]);
        Assert.Equal("secret", settings.Parameters["mssql.password"]);
    }

    [Fact]
    public void Generic_token_maps_to_the_unifi_api_key()
    {
        var settings = WithGeneric("ignored", "ignored", token: "api-key-123");

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, [MonitoringCredentialKind.Unifi]);

        Assert.Equal("api-key-123", settings.Parameters["unifi.apiKey"]);
    }
}
