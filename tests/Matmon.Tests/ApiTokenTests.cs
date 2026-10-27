using System.Text;
using System.Text.Json;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The instance's own API for CI/CD (System > API tokens): tokens that are kept as a hash only, carry scopes and an
/// expiry, can be revoked and never travel in a backup - and the maintenance-window calls a deploy job makes with one.
/// </summary>
public sealed class ApiTokenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-apitoken-" + Guid.NewGuid().ToString("N"));

    public ApiTokenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_secret_is_long_marked_and_only_its_hash_is_needed_to_check_it()
    {
        var secret = ApiTokenSecret.Generate();
        Assert.StartsWith("mmt_", secret);
        Assert.Equal(44, secret.Length);
        Assert.NotEqual(secret, ApiTokenSecret.Generate());

        var hash = ApiTokenSecret.Hash(secret);
        Assert.True(ApiTokenSecret.Matches(hash, secret));
        Assert.True(ApiTokenSecret.Matches(hash, " " + secret + " "));
        Assert.False(ApiTokenSecret.Matches(hash, secret + "x"));
        Assert.Equal(10, ApiTokenSecret.PrefixOf(secret).Length);

        Assert.Equal(secret, ApiTokenSecret.FromHeaders("Bearer " + secret, null));
        Assert.Equal(secret, ApiTokenSecret.FromHeaders(null, secret));
        Assert.Null(ApiTokenSecret.FromHeaders("Basic abc", null));
        Assert.Null(ApiTokenSecret.FromHeaders("Bearer ", null));
    }

    [Fact]
    public void A_token_works_with_its_scope_until_it_is_revoked_and_its_secret_is_never_stored()
    {
        string secret = string.Empty, hash = string.Empty;
        Run(store =>
        {
            var issue = store.CreateApiToken("GitLab deploy", [ApiTokenScopes.Maintenance], DateTimeOffset.UtcNow.AddDays(30), "admin@example.test");
            Assert.Equal(ApiTokenCheckResult.Ok, store.CheckApiToken(issue.Secret, ApiTokenScopes.Maintenance, "10.0.0.5").Result);
            Assert.Equal(ApiTokenCheckResult.ScopeMissing, store.CheckApiToken(issue.Secret, "something-else", null).Result);
            Assert.Equal(ApiTokenCheckResult.Invalid, store.CheckApiToken("mmt_wrong", ApiTokenScopes.Maintenance, null).Result);
            Assert.Equal(ApiTokenCheckResult.Missing, store.CheckApiToken(null, ApiTokenScopes.Maintenance, null).Result);
            Assert.Equal(ApiTokenCheckResult.Missing, store.CheckApiToken("not-a-token", ApiTokenScopes.Maintenance, null).Result);

            var listed = Assert.Single(store.GetApiTokens());
            Assert.Equal("10.0.0.5", listed.LastUsedFrom);
            Assert.NotNull(listed.LastUsedUtc);
            Assert.Equal(issue.Secret[..10], listed.Prefix);

            secret = issue.Secret;
            hash = listed.Hash;
        });

        // what is on disk after the store saved and closed: the hash, never the secret
        var stored = File.ReadAllText(Path.Combine(_dir, "workspace.json"));
        Assert.DoesNotContain(secret, stored);
        Assert.Contains(hash, stored);

        Run(store =>
        {
            var listed = Assert.Single(store.GetApiTokens());
            Assert.Equal(ApiTokenCheckResult.Ok, store.CheckApiToken(secret, ApiTokenScopes.Maintenance, null).Result);
            Assert.True(store.RevokeApiToken(listed.Id));
            Assert.False(store.RevokeApiToken(listed.Id));
            Assert.Equal(ApiTokenCheckResult.Invalid, store.CheckApiToken(secret, ApiTokenScopes.Maintenance, null).Result);
            Assert.True(store.DeleteApiToken(listed.Id));
            Assert.Empty(store.GetApiTokens());
        });
    }

    [Fact]
    public void A_token_is_refused_without_a_name_or_scope_or_with_an_expiry_in_the_past_and_never_rides_in_a_backup()
    {
        Run(store =>
        {
            Assert.Throws<InvalidOperationException>(() => store.CreateApiToken(" ", [ApiTokenScopes.Maintenance], null, null));
            Assert.Throws<InvalidOperationException>(() => store.CreateApiToken("x", [], null, null));
            Assert.Throws<InvalidOperationException>(() => store.CreateApiToken("x", ["admin"], null, null));
            Assert.Throws<InvalidOperationException>(() => store.CreateApiToken("x", [ApiTokenScopes.Maintenance], DateTimeOffset.UtcNow.AddMinutes(-1), null));
            Assert.Throws<InvalidOperationException>(() => store.CreateApiToken("x", [ApiTokenScopes.Maintenance], DateTimeOffset.UtcNow.AddYears(3), null));

            var issue = store.CreateApiToken("Until revoked", [ApiTokenScopes.Maintenance], null, null);
            var hash = store.GetApiTokens().Single().Hash;
            var package = Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.All));
            Assert.DoesNotContain(hash, package);
            Assert.DoesNotContain(issue.Secret, package);
        });
    }

    [Fact]
    public void A_deploy_job_opens_a_window_for_a_tag_or_an_element_and_ends_it()
    {
        Run(store =>
        {
            var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
            var web = store.CreateHost(root.Id, "web-01", "10.0.0.10", null);
            store.CreateHost(root.Id, "db", "10.0.0.20", null);
            store.CreateHost(root.Id, "DB", "10.0.0.21", null);
            var api = new MaintenanceApi(store);
            var now = DateTimeOffset.UtcNow;

            // a tag, defaults: starts now, lasts an hour, named after the target
            var byTag = api.Create(new MaintenanceApiRequest(Target: "tag:web"), "API token \"ci\"", now);
            Assert.Equal(201, byTag.Status);
            var tagged = Read(byTag);
            Assert.Equal("API: #web", tagged.GetProperty("name").GetString());
            Assert.True(tagged.GetProperty("active").GetBoolean());
            Assert.Equal("web", tagged.GetProperty("target").GetProperty("tag").GetString());

            // an element by its exact name, covering it now
            var byName = api.Create(new MaintenanceApiRequest(Name: "Deploy 42", Target: "WEB-01", DurationMinutes: 15), "ci", now);
            Assert.Equal(201, byName.Status);
            var id = Read(byName).GetProperty("id").GetGuid();
            Assert.True(store.IsInMaintenance(web.Id, DateTimeOffset.UtcNow));

            // a name two elements share is refused with the candidates; an unknown one is a 404
            var ambiguous = api.Create(new MaintenanceApiRequest(Target: "db"), "ci", now);
            Assert.Equal(409, ambiguous.Status);
            Assert.Equal(2, Read(ambiguous).GetProperty("candidates").GetArrayLength());
            Assert.Equal(404, api.Create(new MaintenanceApiRequest(Target: "nope"), "ci", now).Status);
            Assert.Equal(400, api.Create(new MaintenanceApiRequest(Target: ""), "ci", now).Status);
            Assert.Equal(400, api.Create(new MaintenanceApiRequest(Target: "web-01", DurationMinutes: 0), "ci", now).Status);
            Assert.Equal(400, api.Create(new MaintenanceApiRequest(Target: "web-01", Start: now.AddHours(-3), End: now.AddHours(-2)), "ci", now).Status);

            // off, on, ended
            Assert.False(Read(api.SetEnabled(id, false, DateTimeOffset.UtcNow)).GetProperty("enabled").GetBoolean());
            Assert.False(store.IsInMaintenance(web.Id, DateTimeOffset.UtcNow));
            Assert.True(Read(api.SetEnabled(id, true, DateTimeOffset.UtcNow)).GetProperty("active").GetBoolean());
            var ended = Read(api.EndNow(id, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.False(ended.GetProperty("active").GetBoolean());
            Assert.False(store.IsInMaintenance(web.Id, DateTimeOffset.UtcNow.AddSeconds(2)));

            Assert.Equal(2, Read(api.List(DateTimeOffset.UtcNow)).GetProperty("items").GetArrayLength());
            Assert.Equal(200, api.Delete(id).Status);
            Assert.Equal(404, api.Delete(id).Status);
            Assert.Equal(404, api.SetEnabled(id, true, DateTimeOffset.UtcNow).Status);
        });
    }

    private static JsonElement Read(ApiAnswer answer) =>
        JsonSerializer.SerializeToElement(answer.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private void Run(Action<InMemoryMonitoringWorkspaceStore> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new ApiTokenTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        body(store);
    }
}

file sealed class ApiTokenTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
