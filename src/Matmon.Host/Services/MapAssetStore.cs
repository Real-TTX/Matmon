using System.Collections.Concurrent;
using System.Security.Cryptography;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// On-disk store for pictures uploaded onto a map (floorplans, rack photos, office plans).
/// <para>
/// Deliberately NOT workspace.json. The branding logo takes the base64-in-JSON route, and that is exactly the
/// pattern not to copy here: workspace.json is fully re-serialised on a 750 ms debounce, copied whole every
/// five minutes and re-parsed on every start. A single 1 MB floorplan becomes ~1.37 MB of JSON rewritten
/// every time someone nudges a pin.
/// </para>
/// Files live under <c>data/uploads</c>, resolved relative to the workspace directory - the same convention
/// as the backup folder and telemetry.db, which matters because <c>data/</c> is the only mounted Docker
/// volume.
/// </summary>
public sealed class MapAssetStore
{
    /// <summary>Nothing in the repo caps an upload today (not branding, not the backup upload, and Kestrel's
    /// 30 MB default is the only brake), so this one is explicit. A floorplan far above this is a photo
    /// nobody downsized, not a plan.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private readonly string _directory;
    private readonly ILogger<MapAssetStore> _logger;

    /// <summary>Assets are immutable once written, so bytes + derived content type + ETag are cached rather
    /// than re-read and re-hashed on every wallboard render.</summary>
    private readonly ConcurrentDictionary<Guid, MapAsset> _cache = new();

    public MapAssetStore(string workspaceDirectory, ILogger<MapAssetStore> logger)
    {
        _directory = Path.Combine(workspaceDirectory, "uploads");
        _logger = logger;
    }

    public string DirectoryPath => _directory;

    /// <summary>Validates and stores <paramref name="content"/>. Returns null and sets
    /// <paramref name="error"/> on rejection - the caller shows that text to the user.</summary>
    public Guid? Save(Stream content, out string? error)
    {
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            // Copy with a hard ceiling rather than trusting a reported length: a chunked upload has none, and
            // Length on a request stream is not guaranteed.
            var chunk = new byte[81920];
            int read;
            while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    error = $"The image is larger than {MaxBytes / (1024 * 1024)} MB.";
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
        {
            error = "The file is empty.";
            return null;
        }

        // Magic bytes, never the file name or the browser-supplied content type. SVG is rejected on purpose:
        // these are served anonymously and same-origin, so a script-bearing SVG would be stored XSS.
        var contentType = BrandingSafety.DetectRasterContentType(bytes);
        if (contentType is null)
        {
            error = "Only PNG and JPEG images are accepted (SVG is not, because it can carry script).";
            return null;
        }

        var id = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(id), bytes);
        _cache[id] = Build(id, bytes, contentType);
        _logger.LogInformation("Stored map asset {AssetId} ({Bytes} bytes, {ContentType}).", id, bytes.Length, contentType);
        error = null;
        return id;
    }

    public MapAsset? Find(Guid id)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        if (_cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Could not read map asset {AssetId}.", id);
            return null;
        }

        // Re-validated on READ as well: the directory is a mounted volume, so what was written is not
        // necessarily what is there now, and the served MIME must come from the bytes either way.
        var contentType = BrandingSafety.DetectRasterContentType(bytes);
        if (contentType is null)
        {
            _logger.LogWarning("Map asset {AssetId} is not a PNG/JPEG and will not be served.", id);
            return null;
        }

        var asset = Build(id, bytes, contentType);
        _cache[id] = asset;
        return asset;
    }

    public bool Delete(Guid id)
    {
        _cache.TryRemove(id, out _);
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Could not delete map asset {AssetId}.", id);
            return false;
        }
    }

    /// <summary>Every stored asset, for a local backup. Skips anything that no longer passes the magic-byte
    /// check rather than carrying it into a snapshot.</summary>
    public IReadOnlyList<MapAsset> EnumerateAll()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var assets = new List<MapAsset>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id) && Find(id) is { } asset)
            {
                assets.Add(asset);
            }
        }

        return assets;
    }

    /// <summary>Writes a restored asset back under its ORIGINAL id - the tiles referencing it were restored
    /// with that id, so a fresh one would orphan every floorplan in the snapshot.</summary>
    public bool Restore(Guid id, byte[] bytes)
    {
        if (id == Guid.Empty || BrandingSafety.DetectRasterContentType(bytes) is not { } contentType)
        {
            return false;
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(id), bytes);
        _cache[id] = Build(id, bytes, contentType);
        return true;
    }

    // The id comes from a parsed Guid, never from user text, so the file name cannot contain a separator or
    // traverse out of the directory.
    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".bin");

    private static MapAsset Build(Guid id, byte[] bytes, string contentType) =>
        new(id, bytes, contentType, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..32] + "\"");
}

/// <param name="ContentType">Derived from the magic bytes, never from the upload's own claim.</param>
public sealed record MapAsset(Guid Id, byte[] Bytes, string ContentType, string ETag);
