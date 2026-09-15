using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>UpsertCloudUser role propagation: a cloud demotion (Owner-&gt;Viewer) must take effect for an SSO-only
/// account (cloud-linked, no local password), but must NOT clobber the locally-managed role of an account that has
/// a local password. This is what makes a cloud-side role change reach the instance over Full Access auto-login.</summary>
public sealed class CloudUserRoleTests : IDisposable
{
    private readonly string _dir;

    public CloudUserRoleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-clouduser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void SsoOnly_user_role_follows_the_cloud_assertion()
    {
        Run(store =>
        {
            // A real instance always has a local admin (first-run setup), so demoting the SSO user leaves an admin
            // in place - without this the "guarantee at least one enabled admin" safety net would re-promote the
            // only user and mask the downgrade.
            store.CreateUser("localadmin@example.com", "S3cret-passphrase!", MatmonUserRole.Admin);

            // First tunnel login provisions an SSO-only (password-less) admin.
            var created = store.UpsertCloudUser("ceo@example.com", MatmonUserRole.Admin);
            Assert.Equal(MatmonUserRole.Admin, created.Role);
            Assert.False(store.HasLocalPassword(created.Id));

            // The cloud demotes them to Viewer -> the next assertion must downgrade the local user.
            var afterDemotion = store.UpsertCloudUser("ceo@example.com", MatmonUserRole.Viewer);
            Assert.Equal(MatmonUserRole.Viewer, afterDemotion.Role);
            Assert.Equal(MatmonUserRole.Viewer, store.FindUser(created.Id)!.Role);
        });
    }

    [Fact]
    public void Local_password_user_keeps_its_locally_managed_role()
    {
        Run(store =>
        {
            // A local account an admin created by hand, with a password and the Admin role.
            var local = store.CreateUser("localadmin@example.com", "S3cret-passphrase!", MatmonUserRole.Admin);
            Assert.True(store.HasLocalPassword(local.Id));

            // The same identity signs in via the cloud, which asserts a lower role. The local role must win - the
            // cloud must not silently strip a role an instance admin set locally.
            var afterCloudLogin = store.UpsertCloudUser("localadmin@example.com", MatmonUserRole.Viewer);
            Assert.Equal(MatmonUserRole.Admin, afterCloudLogin.Role);
            Assert.True(afterCloudLogin.CloudLinked); // still marked cloud-capable
        });
    }

    private void Run(Action<InMemoryMonitoringWorkspaceStore> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new CloudUserTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        body(store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class CloudUserTestHostEnvironment : IHostEnvironment
{
    public CloudUserTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
