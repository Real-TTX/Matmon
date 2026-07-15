using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Store-level guards for the cached managing-partner branding (co-branding v1.1): the disconnect
/// clear and the cheap HasPartner-gated accent accessor the layout themer relies on.</summary>
public sealed class CloudBrandingStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _workspacePath;
    private readonly string _dbPath;

    public CloudBrandingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-cobrand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _workspacePath = Path.Combine(_dir, "workspace.json");
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    private InMemoryMonitoringWorkspaceStore NewStore(ITelemetryRepository telemetry) =>
        new(
            new CobrandTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = _workspacePath },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private static ServicePartnerInfo Partner() => new()
    {
        HasPartner = true,
        Name = "ACME MSP",
        BrandColor = "#AABBCC",
        LogoPng = [1, 2, 3, 4],
        LogoContentType = "image/png",
        ContactUrl = "https://acme.example/support",
    };

    [Fact]
    public void DisconnectCloud_clears_cached_partner_branding()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        store.SetServicePartnerInfo(Partner());
        Assert.NotNull(store.GetServicePartnerInfo());
        Assert.Equal("#AABBCC", store.GetServicePartnerBrandColor());

        store.DisconnectCloud();

        // Once unlinked there is no cloud left to send HasPartner=false, so the cache MUST be cleared here or the
        // stale partner logo/name/accent-colour would keep rendering in the UI + reports forever.
        Assert.Null(store.GetServicePartnerInfo());
        Assert.Null(store.GetServicePartnerBrandColor());
    }

    [Fact]
    public void GetServicePartnerBrandColor_is_null_without_a_managing_partner()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        Assert.Null(store.GetServicePartnerBrandColor());

        // A cached entry that is not an actual managing partner (HasPartner=false) must not theme the app.
        store.SetServicePartnerInfo(new ServicePartnerInfo { HasPartner = false, BrandColor = "#AABBCC" });
        Assert.Null(store.GetServicePartnerBrandColor());
    }

    [Fact]
    public void Branding_suppressed_keeps_the_relationship_but_hides_the_visual_brand()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        var partner = Partner();
        partner.BrandingSuppressed = true;
        store.SetServicePartnerInfo(partner);

        // The relationship + consent tab must survive (customer can still see/revoke management), but the
        // per-render brand accessors (app accent + sidebar "Managed by") report nothing.
        Assert.True(store.GetServicePartnerInfo()?.HasPartner);
        Assert.Null(store.GetServicePartnerBrandColor());
        Assert.Null(store.GetServicePartnerName());
    }

    [Fact]
    public void GetServicePartnerProductName_reflects_white_label_and_suppression()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        var partner = Partner();
        partner.ProductName = "FeuSys Monitoring";
        store.SetServicePartnerInfo(partner);
        Assert.Equal("FeuSys Monitoring", store.GetServicePartnerProductName());

        // Branding opt-out hides the white-label product name too (the relationship stays).
        var suppressed = Partner();
        suppressed.ProductName = "FeuSys Monitoring";
        suppressed.BrandingSuppressed = true;
        store.SetServicePartnerInfo(suppressed);
        Assert.Null(store.GetServicePartnerProductName());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class CobrandTestHostEnvironment : IHostEnvironment
{
    public CobrandTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
