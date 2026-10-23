using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Matmon.Probe.Plugins;

/// <summary>What the instance wants running at one place: an assignment of a plugin, with its settings.</summary>
public sealed record PluginAssignmentSpec(Guid Id, string PluginId, bool Enabled, IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>Everything that needs a restart when it changes.</summary>
    public string Signature => PluginId + "|" + Enabled + "|" + string.Join("|", Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
}

/// <summary>
/// The plugins this host can run: the built-in ones (registered in DI) and the ones loaded at runtime from its plugins
/// directory - one sub-directory or DLL per plugin, each assembly in its own load context, with Matmon's own assemblies
/// shared so <see cref="IMatmonPlugin"/> is the same type on both sides. A plugin DLL runs with the rights of the
/// process: whoever can write into that directory can run code, exactly like installing software. Nothing is ever
/// loaded from the network.
/// </summary>
public sealed class PluginCatalog
{
    private readonly List<IMatmonPlugin> _plugins = [];
    private readonly List<string> _loadErrors = [];

    public PluginCatalog(IEnumerable<IMatmonPlugin> builtIn, ILogger<PluginCatalog> logger, string? pluginsDirectory = null)
    {
        _plugins.AddRange(builtIn);
        if (!string.IsNullOrWhiteSpace(pluginsDirectory) && Directory.Exists(pluginsDirectory))
        {
            LoadExternal(pluginsDirectory, logger);
        }
    }

    public IReadOnlyList<IMatmonPlugin> Plugins => _plugins;

    /// <summary>Plugin files that could not be loaded, in words - shown on the Plugins page.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IMatmonPlugin? Find(string? id) =>
        _plugins.FirstOrDefault(plugin => string.Equals(plugin.Definition.Id, id, StringComparison.OrdinalIgnoreCase));

    private void LoadExternal(string directory, ILogger logger)
    {
        var files = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(directory).Select(sub => Path.Combine(sub, Path.GetFileName(sub) + ".dll")).Where(File.Exists));
        foreach (var file in files)
        {
            try
            {
                var context = new PluginLoadContext(file);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));
                var found = 0;
                foreach (var type in assembly.GetExportedTypes().Where(type => typeof(IMatmonPlugin).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false }))
                {
                    if (Activator.CreateInstance(type) is not IMatmonPlugin plugin)
                    {
                        continue;
                    }

                    if (Find(plugin.Definition.Id) is not null)
                    {
                        _loadErrors.Add($"{Path.GetFileName(file)}: a plugin '{plugin.Definition.Id}' is already loaded - skipped.");
                        continue;
                    }

                    _plugins.Add(plugin);
                    found++;
                    logger.LogInformation("Loaded plugin {Id} {Version} from {File}", plugin.Definition.Id, plugin.Definition.Version, file);
                }

                if (found == 0)
                {
                    _loadErrors.Add($"{Path.GetFileName(file)}: no public plugin class (IMatmonPlugin with a parameterless constructor).");
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _loadErrors.Add($"{Path.GetFileName(file)}: {exception.GetBaseException().Message}");
                logger.LogWarning(exception, "Could not load plugin {File}", file);
            }
        }
    }

    // Each plugin assembly gets its own context (its own dependencies), but Matmon's assemblies and the framework come
    // from the host: a plugin compiled against Matmon.Probe must see the host's IMatmonPlugin, not a copy of it.
    private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } name && (name.StartsWith("Matmon.", StringComparison.Ordinal) || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
            {
                return null; // the host's copy
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

/// <summary>
/// Runs the plugin instances one place (the primary, a probe, an agent) is assigned: <see cref="ReconcileAsync"/> starts
/// what is wanted and not running, stops what is no longer wanted, and restarts an instance whose settings changed. A
/// plugin that fails to start is reported and tried again after <see cref="RetryAfter"/>. Not thread-safe: one loop calls it.
/// </summary>
public sealed class PluginRuntime : IAsyncDisposable
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly PluginCatalog _catalog;
    private readonly IPluginEventSink _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataRoot;
    private readonly string? _probeId;
    private readonly PluginPlacement _here;
    private readonly Dictionary<Guid, Slot> _slots = [];

    public PluginRuntime(PluginCatalog catalog, IPluginEventSink events, ILoggerFactory loggerFactory, string dataRoot, string? probeId)
    {
        _catalog = catalog;
        _events = events;
        _loggerFactory = loggerFactory;
        _dataRoot = dataRoot;
        _probeId = probeId;
        _here = probeId is null ? PluginPlacement.Primary : PluginPlacement.Probe;
    }

    public async Task ReconcileAsync(IReadOnlyList<PluginAssignmentSpec> desired, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var wanted = desired.Where(spec => spec.Enabled).ToDictionary(spec => spec.Id);

        foreach (var (id, slot) in _slots.ToList())
        {
            if (!wanted.TryGetValue(id, out var spec) || spec.Signature != slot.Signature)
            {
                await StopAsync(slot);
                _slots.Remove(id);
            }
        }

        foreach (var spec in wanted.Values)
        {
            if (_slots.TryGetValue(spec.Id, out var existing) && (existing.Instance is not null || now - existing.LastAttemptUtc < RetryAfter))
            {
                continue;
            }

            _slots[spec.Id] = await StartAsync(spec, now, cancellationToken);
        }
    }

    /// <summary>The state of every assignment this place was given (running, error, ...).</summary>
    public IReadOnlyList<(Guid AssignmentId, PluginStatus Status)> Statuses() =>
        _slots.Select(pair => (pair.Key, pair.Value.Instance?.Status ?? pair.Value.Status)).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var slot in _slots.Values)
        {
            await StopAsync(slot);
        }

        _slots.Clear();
    }

    private async Task<Slot> StartAsync(PluginAssignmentSpec spec, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var plugin = _catalog.Find(spec.PluginId);
        if (plugin is null)
        {
            return new Slot(spec.Signature, null, PluginStatus.Error($"This {(_probeId is null ? "instance" : "probe")} has no plugin '{spec.PluginId}' - update it, or install the plugin."), now);
        }

        if ((plugin.Definition.Placement & _here) == 0)
        {
            return new Slot(spec.Signature, null, PluginStatus.Error($"'{plugin.Definition.DisplayName}' cannot run on {(_probeId is null ? "the primary" : "a probe")}."), now);
        }

        var logger = _loggerFactory.CreateLogger("Plugin." + plugin.Definition.Id);
        try
        {
            var directory = Path.Combine(_dataRoot, "plugins-data", plugin.Definition.Id, spec.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var instance = await plugin.StartAsync(new PluginContext
            {
                AssignmentId = spec.Id,
                Settings = spec.Settings,
                Events = new AssignmentSink(_events, spec.Id),
                Logger = logger,
                DataDirectory = directory,
                ProbeId = _probeId
            }, cancellationToken);
            logger.LogInformation("Plugin {Id} started ({Assignment})", plugin.Definition.Id, spec.Id);
            return new Slot(spec.Signature, instance, instance.Status, now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Plugin {Id} did not start: {Message}", plugin.Definition.Id, exception.Message);
            return new Slot(spec.Signature, null, PluginStatus.Error(exception.Message), now);
        }
    }

    private static async Task StopAsync(Slot slot)
    {
        if (slot.Instance is null)
        {
            return;
        }

        try
        {
            await slot.Instance.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A plugin that throws while stopping must not keep the others from reconciling.
            _ = exception;
        }
    }

    private sealed record Slot(string Signature, IPluginInstance? Instance, PluginStatus Status, DateTimeOffset LastAttemptUtc);

    private sealed class AssignmentSink(IPluginEventSink inner, Guid assignmentId) : IPluginEventSink
    {
        public void Publish(PluginEvent pluginEvent) => inner.Publish(pluginEvent with { AssignmentId = assignmentId });
    }
}
