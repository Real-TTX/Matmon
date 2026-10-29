using System.Net;
using System.Security.Cryptography;
using System.Text;
using Matmon.Agent;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

// "Add an agent" in one step: the download carries the instance address and the code, a one-liner fetches a script with
// both baked in. What must hold: the installed agent never keeps the code, the hash/signature still describe the plain
// file, nothing a request sends can break out of a generated script, and the endpoints let in exactly what they should.
public sealed class AgentInstallerTests : IDisposable
{
    private const string Code = "K7QM-4TXC-9RHA-B2NW-P6FE";
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-agent-installer-").FullName;

    [Fact]
    public void TheSettingsRoundTripAndThePlainPackageIsUntouched()
    {
        var plain = RandomNumberGenerator.GetBytes(5000);
        using var combined = new MemoryStream();
        AgentInstallerPayload.Combine(new MemoryStream(plain), new AgentInstallerSettings("https://matmon.example:8099", Code), combined);

        var read = AgentInstallerPayload.Read(combined);

        Assert.NotNull(read);
        Assert.Equal("https://matmon.example:8099", read.Value.Settings.Url);
        Assert.Equal(Code, read.Value.Settings.Code);
        Assert.Equal(plain.Length, read.Value.PlainLength);
        Assert.Equal(plain, combined.ToArray()[..plain.Length]);
    }

    [Fact]
    public void APlainPackageCarriesNoSettings()
    {
        Assert.Null(AgentInstallerPayload.Read(new MemoryStream(RandomNumberGenerator.GetBytes(100))));
        Assert.Null(AgentInstallerPayload.Read(new MemoryStream([1, 2, 3])));

        // The Windows SETUP's own trailer is a different thing and must not be read as an agent download.
        using var setup = new MemoryStream();
        SetupPayload.Combine(new MemoryStream([1, 2, 3, 4]), new MemoryStream([5, 6]), setup);
        Assert.Null(AgentInstallerPayload.Read(setup));
    }

    [Fact]
    public void TheInstalledCopyHasNoCodeAndMatchesThePackagesHash()
    {
        var plain = RandomNumberGenerator.GetBytes(200_000);
        var download = Path.Combine(_dir, "matmon-agent-setup.exe");
        using (var file = File.Create(download))
        {
            AgentInstallerPayload.Combine(new MemoryStream(plain), new AgentInstallerSettings("https://matmon.example", Code), file);
        }

        var installed = Path.Combine(_dir, "installed", "matmon-agent.exe");
        InstallCommand.PlaceSelf(download, installed);

        Assert.Equal(SHA256.HashData(plain), SHA256.HashData(File.ReadAllBytes(installed)));
        Assert.Null(AgentInstallerPayload.ReadFile(installed));
        Assert.DoesNotContain(Code, Encoding.UTF8.GetString(File.ReadAllBytes(installed)));
        Assert.Equal(Code, AgentInstallerPayload.ReadFile(download)?.Code);
    }

    [Fact]
    public void ThePowerShellScriptCarriesAddressCodeAndHash()
    {
        var hash = new string('b', 64);
        var script = AgentInstallScripts.PowerShell("https://matmon.example:8099/", "k7qm4txc9rhab2nwp6fe", hash);

        Assert.Contains("$MatmonUrl = 'https://matmon.example:8099'", script);
        Assert.Contains($"$MatmonCode = '{Code}'", script);
        Assert.Contains($"$Expected = '{hash}'", script);
        Assert.Contains("setup --url $MatmonUrl --code $MatmonCode", script);
    }

    [Fact]
    public void TheShellScriptKnowsEveryUnixPlatformAndItsHash()
    {
        var hashes = new Dictionary<string, string?> { ["linux-x64"] = new string('c', 64), ["osx-arm64"] = new string('d', 64) };
        var script = AgentInstallScripts.Shell("http://192.168.1.10:8099", Code, hashes);

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.DoesNotContain("\r", script);
        Assert.Contains("MATMON_URL='http://192.168.1.10:8099'", script);
        Assert.Contains($"MATMON_CODE='{Code}'", script);
        Assert.Contains($"linux-x64) expected='{new string('c', 64)}'", script);
        Assert.Contains($"osx-arm64) expected='{new string('d', 64)}'", script);
        // A platform the instance has no package for gets an empty hash - the script then says so instead of installing.
        Assert.Contains("linux-arm64) expected=''", script);
        Assert.Contains("Darwin/arm64) rid=osx-arm64", script);
        Assert.Contains("install --url \"$MATMON_URL\" --code \"$MATMON_CODE\"", script);
    }

    [Theory]
    [InlineData("https://matmon.example/'; rm -rf / #")]
    [InlineData("https://matmon.example/$(id)")]
    [InlineData("https://matmon.example/`id`")]
    [InlineData("https://matmon.example/a b")]
    [InlineData("https://matmon.example/?q=1")]
    [InlineData("ftp://matmon.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("matmon.example")]
    [InlineData("")]
    public void AnAddressThatCouldBreakOutOfTheQuotesIsRefused(string url)
    {
        Assert.Null(AgentInstallScripts.SafeUrl(url));
        Assert.Throws<ArgumentException>(() => AgentInstallScripts.PowerShell(url, Code, null));
        Assert.Throws<ArgumentException>(() => AgentInstallScripts.Shell(url, Code, new Dictionary<string, string?>()));
    }

    [Theory]
    [InlineData("K7QM-4TXC-9RHA-B2NW-P6F'")]
    [InlineData("K7QM-4TXC-9RHA-B2NW")]
    [InlineData("K7QM-4TXC-9RHA-B2NW-P6FE-AAAA")]
    [InlineData("O0QM-4TXC-9RHA-B2NW-P6FE")]
    [InlineData("'; Remove-Item C:\\ -Recurse #")]
    public void SomethingThatIsNotACodeIsRefused(string code)
    {
        Assert.Null(AgentEnrollmentCode.Canonical(code));
        Assert.Throws<ArgumentException>(() => AgentInstallScripts.PowerShell("https://matmon.example", code, null));
    }

    [Fact]
    public void TheLaunchdJobRestartsTheAgentAndLetsTheUpdaterSurvive()
    {
        var plist = AgentServiceFiles.LaunchdPlist("/usr/local/bin/matmon-agent");

        Assert.StartsWith("<?xml", plist);
        Assert.Contains("<string>eu.matmon.agent</string>", plist);
        Assert.Contains("<string>/usr/local/bin/matmon-agent</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>\n    <true/>", plist);
        Assert.Contains("<key>KeepAlive</key>\n    <true/>", plist);
        Assert.Contains("<key>AbandonProcessGroup</key>\n    <true/>", plist);
        Assert.Contains($"<key>{ServiceControl.LaunchdLabelVariable}</key>", plist);
        Assert.Contains("/Library/Logs/Matmon Agent/agent.log", plist);
        Assert.Equal(AgentServiceFiles.LaunchdLabelVariable, ServiceControl.LaunchdLabelVariable);
        Assert.Equal(AgentServiceFiles.LaunchdPlistPath, ServiceControl.LaunchdPlistPath(AgentServiceFiles.LaunchdLabel));
        // Well-formed XML, with a path that needs escaping escaped.
        System.Xml.Linq.XDocument.Parse(AgentServiceFiles.LaunchdPlist("/opt/a&b/matmon-agent"), System.Xml.Linq.LoadOptions.None);
    }

    [Fact]
    public void TheSystemdUnitIsTheOneTheRecipeUsedToWrite()
    {
        var unit = AgentServiceFiles.SystemdUnit();

        Assert.Contains("Type=notify", unit);
        Assert.Contains("ExecStart=/usr/local/bin/matmon-agent", unit);
        Assert.Contains("WantedBy=multi-user.target", unit);
        Assert.DoesNotContain("\r", unit);
    }

    [Fact]
    public void EveryPlatformIncludingMacIsOfferedAndTheFeedNamesAreUnique()
    {
        Assert.Equal(["win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"], AgentPackageStore.Platforms.Select(platform => platform.RuntimeId));
        Assert.Equal(5, AgentPackageStore.Platforms.Select(platform => AgentFeed.AssetName(platform.RuntimeId)).Distinct().Count());
        Assert.Equal("matmon-agent-win-x64.exe", AgentFeed.AssetName("win-x64"));
    }

    [Theory]
    [InlineData("win-x64", "FILESRV-01", "matmon-agent-setup-FILESRV-01.exe")]
    [InlineData("win-x64", null, "matmon-agent-setup.exe")]
    [InlineData("linux-arm64", "pi in the hall", "matmon-agent-linux-arm64-pi-in-the-hall")]
    [InlineData("osx-arm64", "../../x", "matmon-agent-osx-arm64-x")]
    public void TheDownloadIsNamedAfterTheMachine(string runtimeId, string? name, string expected)
    {
        Assert.Equal(expected, AgentDownloadEndpoints.InstallerFileName(AgentPackageStore.Platforms.First(platform => platform.RuntimeId == runtimeId), name));
    }

    [Fact]
    public async Task TheEndpointsLetInAValidCodeOnlyAndEmbedItInTheDownload()
    {
        var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        var store = new InMemoryMonitoringWorkspaceStore(new InstallerHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(), new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        var plain = Encoding.UTF8.GetBytes("plain linux agent");
        Directory.CreateDirectory(Path.Combine(_dir, "agent", "linux-x64"));
        File.WriteAllBytes(Path.Combine(_dir, "agent", "linux-x64", "matmon-agent"), plain);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _dir, EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IMonitoringWorkspaceStore>(store);
        builder.Services.AddSingleton(new AgentPackageStore(new InstallerHostEnvironment(_dir), new MatmonRuntimeOptions()));
        builder.Services.AddSingleton<AgentInstallerCodes>();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options => options.AddPolicy("agent-enroll", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("all")));
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapAgentDownloadEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();

        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var issue = store.CreateAgentEnrollment("PI-01", TimeSpan.FromHours(1), "admin");

        async Task<HttpResponseMessage> Get(string path, string? code)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (code is not null)
            {
                request.Headers.Add(AgentDownloadEndpoints.CodeHeader, code);
            }

            return await client.SendAsync(request);
        }

        // No code / a wrong code: refused in words.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/agent/install.sh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/agent/install.sh", "AAAA-BBBB-CCCC-DDDD-EEEE")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/agent/packages/linux-x64", null)).StatusCode);

        // A valid code: the script (with the code, the address it was fetched from and the package's hash) and the download.
        var script = await (await Get("/api/agent/install.sh", issue.Code)).Content.ReadAsStringAsync();
        Assert.Contains($"MATMON_CODE='{issue.Code}'", script);
        Assert.Contains($"MATMON_URL='{address.TrimEnd('/')}'", script);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant(), script);

        var installer = await Get("/api/agent/installer/linux-x64", issue.Code);
        Assert.Equal(HttpStatusCode.OK, installer.StatusCode);
        Assert.Equal("no-store", installer.Headers.CacheControl?.ToString());
        var bytes = await installer.Content.ReadAsByteArrayAsync();
        var embedded = AgentInstallerPayload.Read(new MemoryStream(bytes));
        Assert.Equal(issue.Code, embedded?.Settings.Code);
        Assert.Equal(plain, bytes[..plain.Length]);

        // The browser button: an enrolment id without a session is not enough.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"/api/agent/installer/linux-x64?enrollment={issue.Enrollment.Id}", null)).StatusCode);

        // A used code: the download says so.
        Assert.NotNull(store.RedeemAgentEnrollment(issue.Code, "PI-01").Redemption);
        var used = await Get("/api/agent/installer/linux-x64", issue.Code);
        Assert.Equal(HttpStatusCode.Gone, used.StatusCode);
        Assert.Contains("already used or has expired", await used.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/agent/install.ps1", issue.Code)).StatusCode);

        await app.StopAsync();
        store.Dispose();
        telemetry.Dispose();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

file sealed class InstallerHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
