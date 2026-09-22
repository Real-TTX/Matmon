using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Uploaded map pictures are files, not part of workspace.json, so they have to be pulled into a backup and
/// written back on restore explicitly. Without that a restored map keeps its pins and its layout and comes
/// back with a blank floorplan - which looks like the pins are broken.
/// <para>
/// The second half matters just as much in the other direction: the section must stay OUT of the cloud mask,
/// or a nightly cloud job re-uploads every floorplan on every run.
/// </para>
/// </summary>
public sealed class MapAssetBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-assetbackup-" + Guid.NewGuid().ToString("N"));

    public MapAssetBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup must not fail the run.
        }
    }

    private static byte[] Png()
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private (InMemoryMonitoringWorkspaceStore Store, MapAssetStore Assets, IDisposable Telemetry) NewStore(string name)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var assets = new MapAssetStore(root, NullLogger<MapAssetStore>.Instance);
        var telemetry = new SqliteTelemetryRepository(Path.Combine(root, "t.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new AssetBackupHostEnvironment(root),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(root, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance,
            notificationSink: null,
            mapAssets: assets);
        return (store, assets, telemetry);
    }

    [Fact]
    public void A_local_backup_carries_the_picture_and_a_restore_brings_it_back_under_the_same_id()
    {
        var (source, sourceAssets, sourceTelemetry) = NewStore("source");
        byte[] bytes;
        Guid assetId;
        using (source)
        using (sourceTelemetry)
        {
            assetId = sourceAssets.Save(new MemoryStream(Png()), out _)!.Value;
            source.CreateMap(new MonitoringMap
            {
                Name = "Floorplan",
                Slides =
                [
                    new MonitoringMapSlide
                    {
                        Name = "S1",
                        Tiles = [new MonitoringMapTile { Kind = MonitoringMapTileKind.Image, Title = "Plan", ImageAssetId = assetId }]
                    }
                ]
            });

            bytes = source.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        }

        var (target, targetAssets, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            Assert.Null(targetAssets.Find(assetId));

            target.RestoreBackupBytes(bytes, WorkspaceBackupSection.All);

            // Same id, because the restored tile points at it - a fresh id would orphan the floorplan.
            var restored = targetAssets.Find(assetId);
            Assert.NotNull(restored);
            Assert.Equal("image/png", restored!.ContentType);
            Assert.Equal(assetId, target.GetMaps().Single().Slides[0].Tiles.Single().ImageAssetId);
        }
    }

    [Fact]
    public void The_cloud_config_mask_leaves_pictures_out()
    {
        // Maps ARE in the cloud mask (a board is config); the pictures deliberately are not, because a daily
        // job would push megabytes of unchanged floorplans every night.
        Assert.True(WorkspaceBackupSections.CloudConfig.HasFlag(WorkspaceBackupSection.Maps));
        Assert.False(WorkspaceBackupSections.CloudConfig.HasFlag(WorkspaceBackupSection.MapAssets));
    }

    [Fact]
    public void A_backup_without_the_section_does_not_carry_the_bytes()
    {
        var (store, assets, telemetry) = NewStore("stripped");
        using (store)
        using (telemetry)
        {
            assets.Save(new MemoryStream(Png()), out _);

            var bytes = store.CreateBackupBytes(WorkspaceBackupSections.CloudConfig, "cloud");

            // The package is JSON (possibly compressed) - the cheapest honest assertion is that a config-only
            // snapshot is far smaller than one carrying the image.
            var withAssets = store.CreateBackupBytes(WorkspaceBackupSection.All, "all");
            Assert.True(bytes.Length < withAssets.Length,
                $"config-only package ({bytes.Length} bytes) should be smaller than the full one ({withAssets.Length} bytes)");
        }
    }

    [Fact]
    public void A_tampered_package_cannot_smuggle_a_non_image_into_the_served_directory()
    {
        // The store re-checks the magic bytes on restore, so an SVG swapped into the package by hand is
        // dropped rather than written into a directory that is served anonymously and same-origin.
        var (store, assets, telemetry) = NewStore("tampered");
        using (store)
        using (telemetry)
        {
            var id = Guid.NewGuid();

            Assert.False(assets.Restore(id, System.Text.Encoding.UTF8.GetBytes("<svg onload=alert(1)/>")));
            Assert.Null(assets.Find(id));
        }
    }
}

file sealed class AssetBackupHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
