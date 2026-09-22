using Matmon.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The uploaded-picture store. These assets are served ANONYMOUSLY and SAME-ORIGIN (the public wallboard has
/// to load them), so what gets accepted and what content type is reported are security properties, not
/// cosmetics - an SVG accepted here would be stored XSS on the instance's own origin.
/// </summary>
public sealed class MapAssetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-assets-" + Guid.NewGuid().ToString("N"));

    private MapAssetStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        return new MapAssetStore(_dir, NullLogger<MapAssetStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files must not fail the run.
        }
    }

    private static byte[] Png(int padding = 64)
    {
        var bytes = new byte[8 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    [Fact]
    public void Stores_a_png_and_reports_its_type_from_the_bytes()
    {
        var store = NewStore();

        var id = store.Save(new MemoryStream(Png()), out var error);

        Assert.Null(error);
        Assert.NotNull(id);
        var asset = store.Find(id!.Value);
        Assert.NotNull(asset);
        Assert.Equal("image/png", asset!.ContentType);
    }

    [Fact]
    public void Stores_a_jpeg()
    {
        var store = NewStore();
        var id = store.Save(new MemoryStream(Jpeg()), out _);

        Assert.Equal("image/jpeg", store.Find(id!.Value)!.ContentType);
    }

    [Fact]
    public void Rejects_svg_however_it_is_labelled()
    {
        // The whole point: an SVG can carry script, and these are served same-origin and anonymously.
        var store = NewStore();
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        var id = store.Save(new MemoryStream(svg), out var error);

        Assert.Null(id);
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_anything_that_is_not_a_raster_image()
    {
        var store = NewStore();

        Assert.Null(store.Save(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("GIF89a....")), out _));
        Assert.Null(store.Save(new MemoryStream([0x25, 0x50, 0x44, 0x46]), out _));   // %PDF
        Assert.Null(store.Save(new MemoryStream([]), out _));
    }

    [Fact]
    public void Rejects_a_file_over_the_size_limit_without_buffering_it_whole()
    {
        var store = NewStore();
        var oversized = Png(padding: MapAssetStore.MaxBytes + 1024);

        var id = store.Save(new MemoryStream(oversized), out var error);

        Assert.Null(id);
        Assert.Contains("MB", error);
        // The rejection happens before the directory is even created, so "no file was written" is either an
        // empty folder or no folder at all.
        Assert.True(!Directory.Exists(store.DirectoryPath) || Directory.GetFiles(store.DirectoryPath).Length == 0);
    }

    [Fact]
    public void An_unknown_id_is_simply_not_found()
    {
        var store = NewStore();

        Assert.Null(store.Find(Guid.NewGuid()));
        Assert.Null(store.Find(Guid.Empty));
    }

    [Fact]
    public void Etag_follows_the_content()
    {
        var store = NewStore();
        var first = store.Save(new MemoryStream(Png(16)), out _)!.Value;
        var second = store.Save(new MemoryStream(Png(32)), out _)!.Value;

        Assert.NotEqual(store.Find(first)!.ETag, store.Find(second)!.ETag);
    }

    [Fact]
    public void Delete_removes_the_file_and_the_cached_copy()
    {
        var store = NewStore();
        var id = store.Save(new MemoryStream(Png()), out _)!.Value;

        Assert.True(store.Delete(id));
        Assert.Null(store.Find(id));
        Assert.False(store.Delete(id));
    }

    [Fact]
    public void Restore_keeps_the_original_id_so_tiles_still_point_at_their_picture()
    {
        // A restored tile references the id it was backed up with - minting a fresh one would orphan every
        // floorplan in the snapshot.
        var store = NewStore();
        var id = Guid.NewGuid();

        Assert.True(store.Restore(id, Png()));
        Assert.Equal(id, store.Find(id)!.Id);
        Assert.False(store.Restore(Guid.NewGuid(), System.Text.Encoding.UTF8.GetBytes("<svg/>")));
    }

    [Fact]
    public void EnumerateAll_sees_what_was_saved()
    {
        var store = NewStore();
        store.Save(new MemoryStream(Png(8)), out _);
        store.Save(new MemoryStream(Jpeg()), out _);

        Assert.Equal(2, store.EnumerateAll().Count);
    }
}
