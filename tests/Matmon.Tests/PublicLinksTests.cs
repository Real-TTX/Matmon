using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Public links must be FULL addresses - through the cloud console the request knows only the internal host,
/// and a bare path used to be what the copy button copied.</summary>
public sealed class PublicLinksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-publiclinks-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;

    public PublicLinksTests()
    {
        Directory.CreateDirectory(_dir);
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        _store = new InMemoryMonitoringWorkspaceStore(
            new PublicLinksHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _telemetry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static HttpContext Request(string host, bool tunneled = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString(host);
        if (tunneled)
        {
            http.Request.Headers["X-Matmon-Embed"] = "1";
        }

        return http;
    }

    [Fact]
    public void A_direct_visit_gives_its_own_address_and_the_console_reuses_it()
    {
        Assert.Equal("http://nas.local:8099/Maps/Public/abc", PublicLinks.Absolute(Request("nas.local:8099"), _store, "/Maps/Public/abc"));

        Assert.Equal("http://nas.local:8099/Maps/Public/abc", PublicLinks.Absolute(Request("localhost:8099", tunneled: true), _store, "/Maps/Public/abc"));
    }

    [Fact]
    public void Inside_the_console_with_nothing_known_there_is_no_address_rather_than_a_wrong_one()
    {
        Assert.Null(PublicLinks.Absolute(Request("localhost:8099", tunneled: true), _store, "/status/x"));
    }

    [Fact]
    public void A_set_address_wins_and_a_localhost_visit_never_replaces_a_real_one()
    {
        PublicLinks.Absolute(Request("nas.local:8099"), _store, "/");
        PublicLinks.Absolute(Request("localhost:8099"), _store, "/");
        Assert.Equal("http://nas.local:8099", _store.GetPublicAddress().Remembered);

        Assert.Null(_store.SetPublicBaseUrl("https://monitor.example.com/"));
        Assert.Equal("https://monitor.example.com/status/x", PublicLinks.Absolute(Request("nas.local:8099"), _store, "/status/x"));
        Assert.NotNull(_store.SetPublicBaseUrl("monitor.example.com"));
    }
}

file sealed class PublicLinksHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
