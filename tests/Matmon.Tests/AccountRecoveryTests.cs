using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// An instance set up with a Matmon Cloud account has no local password - so there must always be a way in that does not
/// depend on the cloud: the recovery file in the data folder. And whoever signs in through the cloud may switch their local
/// password off, but only while that way in is really there.
/// </summary>
public sealed class AccountRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-recovery-" + Guid.NewGuid().ToString("N"));

    public AccountRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Setup_with_the_cloud_makes_the_connecting_account_a_passwordless_administrator()
    {
        Run(store =>
        {
            Assert.True(store.IsSetupRequired());
            var admin = store.CompleteInitialSetupWithCloud("owner@example.test");

            Assert.False(store.IsSetupRequired());
            Assert.Equal(MatmonUserRole.Admin, admin.Role);
            Assert.True(admin.CloudLinked);
            Assert.False(store.HasLocalPassword(admin.Id));
            Assert.False(store.AnyLocalSignIn());
            Assert.Throws<InvalidOperationException>(() => store.CompleteInitialSetupWithCloud("other@example.test"));
        });
    }

    [Fact]
    public void A_recovery_file_resets_the_administrator_and_switches_its_second_factor_off()
    {
        Run(store =>
        {
            var admin = store.CompleteInitialSetupWithCloud("owner@example.test");
            store.BeginTotpEnrollment(admin.Id, "Matmon");

            var recovered = store.ApplyRecoveryAdmin(null, "a-new-password-1");

            Assert.Equal(admin.Id, recovered.Id); // no address given: the first administrator
            Assert.False(recovered.TwoFactorEnabled);
            Assert.NotNull(store.ValidateUser("owner@example.test", "a-new-password-1"));
            Assert.True(store.AnyLocalAdminSignIn());
        });
    }

    [Fact]
    public void A_recovery_file_on_a_fresh_instance_completes_the_setup()
    {
        Run(store =>
        {
            store.ApplyRecoveryAdmin(null, "a-new-password-1");

            Assert.False(store.IsSetupRequired());
            Assert.NotNull(store.ValidateUser(RecoveryAdmin.DefaultEmail, "a-new-password-1"));
        });
    }

    [Fact]
    public void The_service_applies_the_file_deletes_it_and_writes_the_password_down()
    {
        Run(store =>
        {
            File.WriteAllText(Path.Combine(_dir, RecoveryAdmin.PlainTrigger), "rescue@example.test\n");
            var service = new RecoveryAdminService(_dir, store, NullLogger<RecoveryAdminService>.Instance);

            var result = service.ApplyPending();

            Assert.NotNull(result);
            Assert.False(File.Exists(Path.Combine(_dir, RecoveryAdmin.PlainTrigger)));
            var written = File.ReadAllText(Path.Combine(_dir, RecoveryAdmin.ResultFile));
            var password = written.Split('\n').Single(line => line.StartsWith("Password: ", StringComparison.Ordinal))["Password: ".Length..].Trim();
            Assert.NotNull(store.ValidateUser("rescue@example.test", password));
            Assert.Null(service.ApplyPending()); // nothing left to do
        });
    }

    [Fact]
    public void A_json_file_with_a_password_that_is_too_short_is_refused_and_still_deleted()
    {
        Run(store =>
        {
            File.WriteAllText(Path.Combine(_dir, RecoveryAdmin.JsonTrigger), "{\"email\":\"a@example.test\",\"password\":\"short\"}");
            var service = new RecoveryAdminService(_dir, store, NullLogger<RecoveryAdminService>.Instance);

            Assert.StartsWith("Not applied", service.ApplyPending());
            Assert.False(File.Exists(Path.Combine(_dir, RecoveryAdmin.JsonTrigger)));
            Assert.True(store.IsSetupRequired());
        });
    }

    [Theory]
    [InlineData("recovery-admin.json", "{\"Email\":\"x@example.test\",\"Password\":\"long-enough-1\"}", "x@example.test", "long-enough-1")]
    [InlineData("recovery-admin.json", "", null, null)]
    [InlineData("recovery-admin", "", null, null)]
    [InlineData("recovery-admin", "\n  y@example.test \nignored", "y@example.test", null)]
    public void Recovery_files_are_read(string name, string content, string? email, string? password)
    {
        var request = RecoveryAdmin.Parse(name, content, out var error);

        Assert.Null(error);
        Assert.Equal(new RecoveryAdminRequest(email, password), request);
    }

    [Fact]
    public void A_broken_json_file_says_why()
    {
        Assert.Null(RecoveryAdmin.Parse(RecoveryAdmin.JsonTrigger, "{not json", out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void The_local_password_goes_only_for_a_cloud_account_while_the_cloud_link_is_there()
    {
        Run(store =>
        {
            var admin = store.CompleteInitialSetup("owner@example.test", "local-password-1");

            // Never signed in through the cloud, no cloud link: refused.
            Assert.Equal(RemovePasswordResult.NoCloudSignIn, store.RemoveOwnPassword(admin.Id, "local-password-1"));

            store.UpsertCloudUser("owner@example.test", MatmonUserRole.Admin); // signed in with the cloud once
            Assert.Equal(RemovePasswordResult.NoCloudSignIn, store.RemoveOwnPassword(admin.Id, "local-password-1"));

            store.SetCloudConnectionSettings("https://cloud.example.test", Guid.NewGuid().ToString(), "token", enabled: true);
            Assert.Equal(RemovePasswordResult.WrongCurrent, store.RemoveOwnPassword(admin.Id, "wrong"));
            Assert.Equal(RemovePasswordResult.Success, store.RemoveOwnPassword(admin.Id, "local-password-1"));

            Assert.False(store.HasLocalPassword(admin.Id));
            Assert.Null(store.ValidateUser("owner@example.test", "local-password-1"));
            Assert.False(store.AnyLocalAdminSignIn());
        });
    }

    private void Run(Action<InMemoryMonitoringWorkspaceStore> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new RecoveryTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        body(store);
    }
}

file sealed class RecoveryTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
