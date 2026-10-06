using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record MibUploadResult(IReadOnlyList<string> Added, IReadOnlyList<string> Rejected);

/// <summary>
/// The instance's MIB set: the standard MIBs shipped with the image (<c>mibs/</c> next to the binaries) plus the
/// ones an admin uploads (<c>data/mibs</c>, in the one mounted Docker volume). Names are resolved HERE, on the
/// instance, when a walk is shown - never on the probe: a walk runs wherever the probe is, and translating on
/// display means no MIB is ever shipped to a probe and a fresh upload relabels results that already exist.
///
/// The registry is rebuilt on every change and swapped in whole, so readers never see a half-loaded set and need
/// no lock. An uploaded module replaces a shipped one of the same name (a newer vendor revision wins).
/// </summary>
public sealed partial class MibLibrary
{
    public const long MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxArchiveBytes = 20 * 1024 * 1024;
    private const int MaxArchiveEntries = 500;

    private readonly string _builtInDirectory;
    private readonly ILogger<MibLibrary> _logger;
    private readonly object _writeGate = new();
    private volatile MibRegistry _registry = MibRegistry.Empty;
    private IReadOnlyDictionary<string, DateTimeOffset> _uploadedAt = new Dictionary<string, DateTimeOffset>();

    public MibLibrary(string workspaceDirectory, string builtInDirectory, ILogger<MibLibrary> logger)
    {
        UploadDirectory = Path.Combine(workspaceDirectory, "mibs");
        _builtInDirectory = builtInDirectory;
        _logger = logger;
        Reload();
    }

    public string UploadDirectory { get; }

    public MibRegistry Registry => _registry;

    /// <summary>When each uploaded module's file was written (for the MIB page).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> UploadedAt => _uploadedAt;

    public MibTranslation? Translate(string? oid) => _registry.Translate(oid);

    public void Reload()
    {
        var modules = new List<(MibModule, string)>();
        modules.AddRange(LoadDirectory(_builtInDirectory, "Built-in", out _));
        modules.AddRange(LoadDirectory(UploadDirectory, "Uploaded", out var uploadedAt));
        _uploadedAt = uploadedAt;
        _registry = new MibRegistry(modules);
        _logger.LogInformation("Loaded {Modules} MIB modules with {Nodes} nodes.", _registry.Modules.Count, _registry.NodeCount);
    }

    private List<(MibModule, string)> LoadDirectory(string directory, string source, out IReadOnlyDictionary<string, DateTimeOffset> writtenAt)
    {
        var result = new List<(MibModule, string)>();
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        writtenAt = times;
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var module in MibParser.Parse(Decode(File.ReadAllBytes(file))))
                {
                    result.Add((module, source));
                    times[module.Name] = File.GetLastWriteTimeUtc(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "MIB file {File} could not be read.", file);
            }
        }
        return result;
    }

    /// <summary>
    /// Stores uploaded MIB files (plain or inside a .zip). Each file must contain at least one MIB module; it is
    /// saved under its first module's name, so uploading a newer revision replaces the old one.
    /// </summary>
    public MibUploadResult Upload(IEnumerable<(string FileName, byte[] Content)> files)
    {
        var added = new List<string>();
        var rejected = new List<string>();
        var texts = new List<(string FileName, string Text)>();

        foreach (var (fileName, content) in files)
        {
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ExtractArchive(fileName, content, texts, rejected);
            }
            else if (content.LongLength > MaxFileBytes)
            {
                rejected.Add($"{fileName}: larger than {MaxFileBytes / 1024 / 1024} MB - that is not a MIB.");
            }
            else
            {
                texts.Add((fileName, Decode(content)));
            }
        }

        lock (_writeGate)
        {
            Directory.CreateDirectory(UploadDirectory);
            foreach (var (fileName, text) in texts)
            {
                var modules = MibParser.Parse(text);
                if (modules.Count == 0)
                {
                    rejected.Add($"{fileName}: no MIB module found (expected \"NAME DEFINITIONS ::= BEGIN\").");
                    continue;
                }
                var name = modules[0].Name;
                if (!SafeModuleName().IsMatch(name))
                {
                    rejected.Add($"{fileName}: the module name '{name}' is not usable as a file name.");
                    continue;
                }
                File.WriteAllText(Path.Combine(UploadDirectory, name + ".mib"), text, Encoding.UTF8);
                added.AddRange(modules.Select(module => module.Name));
            }

            if (added.Count > 0)
            {
                Reload();
            }
        }

        return new MibUploadResult(added, rejected);
    }

    /// <summary>Removes an uploaded module. Shipped modules cannot be deleted (only overridden).</summary>
    public bool Delete(string moduleName)
    {
        if (!SafeModuleName().IsMatch(moduleName))
        {
            return false;
        }
        lock (_writeGate)
        {
            var path = Path.Combine(UploadDirectory, moduleName + ".mib");
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            Reload();
            return true;
        }
    }

    private static void ExtractArchive(string fileName, byte[] content, List<(string, string)> texts, List<string> rejected)
    {
        if (content.LongLength > MaxArchiveBytes)
        {
            rejected.Add($"{fileName}: archive larger than {MaxArchiveBytes / 1024 / 1024} MB.");
            return;
        }
        try
        {
            using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var entries = archive.Entries.Where(entry => entry.Length > 0 && !entry.FullName.EndsWith('/')).ToList();
            if (entries.Count > MaxArchiveEntries)
            {
                rejected.Add($"{fileName}: more than {MaxArchiveEntries} files.");
                return;
            }
            long total = 0;
            foreach (var entry in entries)
            {
                // Length is the declared size; read with a hard cap anyway, so a zip bomb cannot lie its way in.
                if (entry.Length > MaxFileBytes || (total += entry.Length) > MaxArchiveBytes)
                {
                    rejected.Add($"{fileName}/{entry.FullName}: too large.");
                    continue;
                }
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxFileBytes)
                    {
                        break;
                    }
                }
                if (buffer.Length > MaxFileBytes)
                {
                    rejected.Add($"{fileName}/{entry.FullName}: too large.");
                    continue;
                }
                texts.Add(($"{fileName}/{entry.FullName}", Decode(buffer.ToArray())));
            }
        }
        catch (InvalidDataException)
        {
            rejected.Add($"{fileName}: not a valid zip archive.");
        }
    }

    /// <summary>MIBs are ASCII in theory; vendors ship UTF-8 and Latin-1 in practice.</summary>
    private static string Decode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content);
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex SafeModuleName();
}
