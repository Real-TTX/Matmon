using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Whoever opens an instance through Matmon Cloud as its managing partner or as Matmon Support signs in as ONE shared user
/// (the partner's name / "Matmon Support"), so the instance shows that the partner and Matmon have access - not which of
/// their staff opened it. Members of the instance keep a user of their own.
/// </summary>
public sealed class RemoteAccessUserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-remote-access-" + Guid.NewGuid().ToString("N"));

    public RemoteAccessUserTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void The_cloud_header_names_the_partner_or_support()
    {
        var partnerId = Guid.NewGuid();
        Assert.True(RemoteAccess.TryParse($"partner|{partnerId}|{Uri.EscapeDataString("FeuSys GmbH & Co.")}", out var key, out var name));
        Assert.Equal("partner:" + partnerId, key);
        Assert.Equal("FeuSys GmbH & Co.", name);

        Assert.True(RemoteAccess.TryParse("support|Matmon%20Support", out key, out name));
        Assert.Equal(RemoteAccess.SupportKey, key);
        Assert.Equal("Matmon Support", name);

        Assert.False(RemoteAccess.TryParse("", out _, out _));
        Assert.False(RemoteAccess.TryParse("member", out _, out _));
        Assert.False(RemoteAccess.TryParse("partner|not-a-guid|X", out _, out _));
        Assert.True(RemoteAccess.TryParse($"partner|{partnerId}|%0A%0D", out _, out name));
        Assert.Equal("Partner", name); // control characters only - never an empty name
    }

    [Fact]
    public void Two_people_of_the_partner_become_one_user_without_an_address()
    {
        Run(store =>
        {
            store.CompleteInitialSetup("owner@example.test", "owner-password-1");
            var key = "partner:" + Guid.NewGuid();

            var first = store.UpsertRemoteAccessUser(key, "FeuSys", MatmonUserRole.Admin, "anna@feusys.example");
            var second = store.UpsertRemoteAccessUser(key, "FeuSys", MatmonUserRole.Admin, "ben@feusys.example");

            Assert.Equal(first.Id, second.Id);
            Assert.Equal("FeuSys", second.Username);
            Assert.Equal(string.Empty, second.Email);
            Assert.Single(store.GetUsers(), user => user.RemoteAccessKey == key);
            Assert.DoesNotContain("feusys", store.ResolveBuiltInRecipients(NotificationReceiverDefaults.AllUsersReceiverId), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ChangePasswordResult.NotAllowed, store.ChangeOwnPassword(second.Id, null, "a-new-password-1"));
            Assert.False(store.HasLocalPassword(second.Id));

            // The partner renames itself in the cloud - the user follows.
            Assert.Equal("FeuSys Systems", store.UpsertRemoteAccessUser(key, "FeuSys Systems", MatmonUserRole.Admin, "anna@feusys.example").Username);
        });
    }

    [Fact]
    public void The_personal_user_an_earlier_access_created_is_replaced()
    {
        Run(store =>
        {
            store.CompleteInitialSetup("owner@example.test", "owner-password-1");
            store.UpsertCloudUser("support-person@matmon.example", MatmonUserRole.Admin);

            store.UpsertRemoteAccessUser(RemoteAccess.SupportKey, RemoteAccess.SupportName, MatmonUserRole.Admin, "support-person@matmon.example");

            Assert.DoesNotContain(store.GetUsers(), user => user.Email == "support-person@matmon.example");
            Assert.Contains(store.GetUsers(), user => user.Username == "Matmon Support" && user.RemoteAccessKey == RemoteAccess.SupportKey);
            // The owner's own account (it has a password) is never touched, even under the same address.
            Assert.Contains(store.GetUsers(), user => user.Email == "owner@example.test");
        });
    }

    [Fact]
    public void A_local_account_with_the_same_name_keeps_its_name()
    {
        Run(store =>
        {
            store.CompleteInitialSetup("owner@example.test", "owner-password-1");
            store.CreateUser("FeuSys", "a-password-123", MatmonUserRole.Viewer);

            var shared = store.UpsertRemoteAccessUser("partner:" + Guid.NewGuid(), "FeuSys", MatmonUserRole.Admin, "anna@feusys.example");

            Assert.Equal("FeuSys (2)", shared.Username);
        });
    }

    private void Run(Action<InMemoryMonitoringWorkspaceStore> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new RemoteAccessTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        body(store);
    }
}

file sealed class RemoteAccessTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
