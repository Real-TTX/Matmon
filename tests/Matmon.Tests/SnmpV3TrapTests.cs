using System.Net;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Core.Telemetry;
using Matmon.Host.Pages;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// SNMPv3 traps (USM): a trap is checked against the configured user with keys localised to the SENDER's engine id -
/// signed only, or signed and encrypted - and refused for a wrong password, an unknown user, or less protection than the
/// user is set up for. The users' passwords are encrypted at rest and the form keeps a password left empty.
/// </summary>
public sealed class SnmpV3TrapTests : IDisposable
{
    private const string OnBattery = "1.3.6.1.2.1.33.2.1";
    private static readonly byte[] EngineId = Convert.FromHexString("80001F88804D61746D6F6E5465737431");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-v3traps-" + Guid.NewGuid().ToString("N"));

    public SnmpV3TrapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static SnmpTrapV3User User(string auth = "sha1", string priv = "none", string name = "ups-monitor") => new()
    {
        Username = name,
        AuthProtocol = auth,
        AuthPassword = "auth-secret-1",
        PrivProtocol = priv,
        PrivPassword = priv == "none" ? null : "priv-secret-1"
    };

    private static byte[] Trap(SnmpTrapV3User sender) =>
        SnmpTrapParser.BuildV3Trap(sender, EngineId, engineBoots: 3, engineTime: 4711, OnBattery, TimeSpan.FromSeconds(42), [("1.3.6.1.2.1.33.1.2.3.0", "17")]);

    [Theory]
    [InlineData("md5", "none")]
    [InlineData("sha1", "none")]
    [InlineData("sha256", "none")]
    [InlineData("sha1", "des")]
    [InlineData("sha1", "aes128")]
    [InlineData("sha256", "aes256")]
    [InlineData("sha512", "aes256c")]
    [InlineData("sha1", "3des")]
    public void A_signed_or_encrypted_trap_of_a_known_user_is_read(string auth, string priv)
    {
        var user = User(auth, priv);

        Assert.True(SnmpTrapParser.TryParse(Trap(user), [user], out var trap, out var error), error);
        Assert.Equal(3, trap!.Version);
        Assert.Equal("ups-monitor", trap.Community);
        Assert.Equal(OnBattery, trap.TrapOid);
        Assert.Equal(42, trap.Uptime!.Value.TotalSeconds, 2);
        Assert.Equal("17", Assert.Single(trap.VarBinds).Value);
    }

    [Fact]
    public void A_wrong_password_fails_authentication()
    {
        var device = User("sha256", "aes128");
        var configured = User("sha256", "aes128");
        configured.AuthPassword = "another-password";

        Assert.False(SnmpTrapParser.TryParse(Trap(device), [configured], out _, out var error));
        Assert.Contains("failed authentication", error);
    }

    [Fact]
    public void A_wrong_privacy_password_does_not_decode_into_a_trap()
    {
        var device = User("sha1", "aes128");
        var configured = User("sha1", "aes128");
        configured.PrivPassword = "another-password";

        Assert.False(SnmpTrapParser.TryParse(Trap(device), [configured], out var trap, out _));
        Assert.Null(trap);
    }

    [Fact]
    public void An_unknown_user_or_no_users_at_all_is_refused_in_words()
    {
        var device = User(name: "stranger");

        Assert.False(SnmpTrapParser.TryParse(Trap(device), [User()], out _, out var unknown));
        Assert.Contains("unknown user 'stranger'", unknown);
        Assert.False(SnmpTrapParser.TryParse(Trap(device), [], out _, out var none));
        Assert.Contains("no SNMPv3 user is set up", none);
    }

    [Fact]
    public void Less_protection_than_the_user_is_set_up_for_is_refused()
    {
        // The device signs but does not encrypt; the user here demands encryption - a downgrade.
        var device = User("sha1", "none");
        var configured = User("sha1", "aes128");

        Assert.False(SnmpTrapParser.TryParse(Trap(device), [configured], out _, out var error));
        Assert.Contains("less protected", error);

        // Unsigned against a signing user.
        var unsigned = User("none", "none");
        Assert.False(SnmpTrapParser.TryParse(Trap(unsigned), [User("sha1")], out _, out _));
    }

    [Fact]
    public void A_tampered_trap_fails_authentication()
    {
        var user = User("sha1", "none");
        var bytes = Trap(user);
        bytes[^1] ^= 0x01; // the last byte of the last binding's value

        Assert.False(SnmpTrapParser.TryParse(bytes, [user], out _, out var error));
        Assert.Contains("failed authentication", error);
    }

    [Fact]
    public async Task A_v3_trap_passes_the_receiver_without_a_community_and_lands_in_the_log()
    {
        using var store = new SyslogStore(Path.Combine(_dir, "log.db"), NullLogger<SyslogStore>.Instance);
        var state = new SnmpTrapReceiverState();
        var ingest = new SnmpTrapIngest(store, state, NullLogger<SnmpTrapIngest>.Instance);
        var user = User("sha256", "aes128");
        // Communities only concern v1/v2c: the v3 user passed a real check.
        ingest.Configure(["only-this-community"], pollOnTrap: false, [user]);

        Assert.True(ingest.Accept(Trap(user), IPAddress.Parse("192.0.2.10"), DateTimeOffset.UtcNow));
        await store.FlushAsync();

        var message = Assert.Single(store.Search(new SyslogQuery { Contains = "SNMP trap" }, 10));
        Assert.Contains("upsTrapOnBattery", message.Message);
        Assert.Equal(1, state.Snapshot().Received);
    }

    // --- the form ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_form_keeps_a_stored_password_left_empty_and_follows_a_rename()
    {
        var stored = new[] { User("sha256", "aes128") };
        SnmpTrapV3UserInput[] rows =
        [
            new() { OriginalUsername = "ups-monitor", Username = "ups-renamed", AuthProtocol = "sha256", PrivProtocol = "aes128" },
            new() { Username = "" } // the empty "add" row
        ];

        Assert.True(SystemSnmpTrapsModel.TryBuildV3Users(rows, stored, out var users, out var error), error);
        var user = Assert.Single(users);
        Assert.Equal("ups-renamed", user.Username);
        Assert.Equal("auth-secret-1", user.AuthPassword);
        Assert.Equal("priv-secret-1", user.PrivPassword);
    }

    [Theory]
    [InlineData("none", "aes128", "auth-secret-1", "privacy needs authentication")]
    [InlineData("sha1", "none", "", "enter the authentication password")]
    [InlineData("sha1", "none", "short", "at least 8 characters")]
    [InlineData("sha999", "none", "auth-secret-1", "unknown authentication")]
    public void The_form_refuses_a_user_no_device_could_match(string auth, string priv, string password, string expected)
    {
        SnmpTrapV3UserInput[] rows = [new() { Username = "nas", AuthProtocol = auth, PrivProtocol = priv, AuthPassword = password }];

        Assert.False(SystemSnmpTrapsModel.TryBuildV3Users(rows, [], out _, out var error));
        Assert.Contains(expected, error);
    }

    // --- at rest -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_passwords_are_encrypted_at_rest_and_come_back_after_a_restart()
    {
        var dp = new EphemeralDataProtectionProvider();
        var wsPath = Path.Combine(_dir, "ws.json");
        InMemoryMonitoringWorkspaceStore NewStore(ITelemetryRepository telemetry) => new(
            new V3TrapHostEnvironment(_dir), new MatmonRuntimeOptions { WorkspacePath = wsPath }, new MatmonAuthOptions(), dp, telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

        using (var t1 = new SqliteTelemetryRepository(Path.Combine(_dir, "t1.db")))
        using (var s1 = NewStore(t1))
        {
            var settings = s1.GetSnmpTrapSettings();
            settings.V3Users = [User("sha256", "aes128")];
            s1.UpdateSnmpTrapSettings(settings);
        }

        var json = File.ReadAllText(wsPath);
        Assert.DoesNotContain("auth-secret-1", json);
        Assert.DoesNotContain("priv-secret-1", json);
        Assert.Contains("ups-monitor", json);

        using (var t2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db")))
        using (var s2 = NewStore(t2))
        {
            var user = Assert.Single(s2.GetSnmpTrapSettings().V3Users);
            Assert.Equal("auth-secret-1", user.AuthPassword);
            Assert.Equal("priv-secret-1", user.PrivPassword);
        }
    }
}

file sealed class V3TrapHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
