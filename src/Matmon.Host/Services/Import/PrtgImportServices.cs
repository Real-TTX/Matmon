using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using Matmon.Core.Domain;
using Matmon.Core.Import;

namespace Matmon.Host.Services.Import;

/// <summary>How to reach a PRTG server: its address and either an API token (PRTG 22.1+) or a user with its passhash or password.</summary>
public sealed record PrtgConnection(string BaseUrl, string? ApiToken, string? Username, string? Passhash, string? Password, bool IgnoreCertificateErrors)
{
    /// <summary>The credential part of an API query (never logged).</summary>
    public string AuthQuery()
    {
        if (!string.IsNullOrWhiteSpace(ApiToken))
        {
            return "apitoken=" + Uri.EscapeDataString(ApiToken.Trim());
        }
        var user = "username=" + Uri.EscapeDataString(Username?.Trim() ?? string.Empty);
        return !string.IsNullOrWhiteSpace(Passhash)
            ? user + "&passhash=" + Uri.EscapeDataString(Passhash.Trim())
            : user + "&password=" + Uri.EscapeDataString(Password ?? string.Empty);
    }
}

public sealed class PrtgException(string message) : Exception(message);

/// <summary>
/// The two PRTG API calls the import needs: the sensor tree (<c>/api/table.xml?content=sensortree</c>) and one property of
/// an object (<c>/api/getobjectproperty.htm</c>). A self-signed certificate - common on a PRTG core - is accepted only when the
/// admin ticked it. Errors come back in words (wrong credentials, not PRTG, unreachable), never with the credentials in them.
/// </summary>
public sealed class PrtgClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PrtgConnection _connection;

    public PrtgClient(PrtgConnection connection, HttpMessageHandler? handler = null)
    {
        _connection = connection;
        handler ??= new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = connection.IgnoreCertificateErrors
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
    }

    private string Url(string path, string query) =>
        $"{_connection.BaseUrl.TrimEnd('/')}{path}?{query}&{_connection.AuthQuery()}";

    public async Task<IReadOnlyList<PrtgNode>> GetSensorTreeAsync(CancellationToken cancellationToken)
    {
        var xml = await GetAsync(Url("/api/table.xml", "content=sensortree"), cancellationToken);
        try
        {
            var tree = PrtgSensorTree.Parse(xml);
            if (tree.Count == 0)
            {
                throw new PrtgException("The server answered, but with no sensor tree - is this a PRTG server, and may this user see any objects?");
            }
            return tree;
        }
        catch (System.Xml.XmlException)
        {
            throw new PrtgException("The server did not answer with PRTG's sensor tree (not XML). Check the address.");
        }
    }

    /// <summary>One property of an object, or null when PRTG does not have it.</summary>
    public async Task<string?> GetPropertyAsync(int objectId, string name, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await GetAsync(Url("/api/getobjectproperty.htm",
                $"id={objectId.ToString(CultureInfo.InvariantCulture)}&name={Uri.EscapeDataString(name)}&show=nohtmlencode"), cancellationToken);
        }
        catch (PrtgException)
        {
            return null;
        }
        try
        {
            var result = XDocument.Parse(body).Root?.Element("result")?.Value?.Trim();
            return string.IsNullOrEmpty(result) || result.StartsWith("(Property not found", StringComparison.OrdinalIgnoreCase) ? null : result;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PrtgException($"The PRTG server could not be reached: {ex.Message.Replace(_connection.AuthQuery(), "…", StringComparison.Ordinal)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PrtgException("The PRTG server did not answer in time.");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PrtgException("PRTG refused the login - check the API token, or the user name and passhash / password.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new PrtgException($"The PRTG server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The import between "connect" and "import": the connection (its token only ever here, in memory, never in the page) and the
/// tree that was read. A random id names it in the address; it lives 30 minutes and belongs to the admin who started it.
/// </summary>
public sealed class PrtgImportSessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, PrtgImportSession> _sessions = new(StringComparer.Ordinal);

    public string Create(PrtgConnection connection, IReadOnlyList<PrtgNode> tree, string owner)
    {
        Prune();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _sessions[id] = new PrtgImportSession(connection, tree, owner, DateTimeOffset.UtcNow + Lifetime);
        return id;
    }

    public PrtgImportSession? Get(string? id, string owner)
    {
        Prune();
        return id is not null && _sessions.TryGetValue(id, out var session) && string.Equals(session.Owner, owner, StringComparison.OrdinalIgnoreCase)
            ? session
            : null;
    }

    public void Remove(string id) => _sessions.TryRemove(id, out _);

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, session) in _sessions)
        {
            if (session.ExpiresUtc <= now)
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }
}

public sealed record PrtgImportSession(PrtgConnection Connection, IReadOnlyList<PrtgNode> Tree, string Owner, DateTimeOffset ExpiresUtc);

/// <summary>What an import did, for the page.</summary>
public sealed class PrtgImportReport
{
    public int FoldersCreated { get; set; }
    public int HostsCreated { get; set; }
    public int SensorsCreated { get; set; }
    public int SensorsSkippedExisting { get; set; }
    public string? LimitReason { get; set; }
    /// <summary>Created sensors that need something added before they work: path and what.</summary>
    public List<(string Path, string Note)> ToComplete { get; } = [];
    public List<(string Path, string Note)> NotImported { get; } = [];
}

/// <summary>
/// Turns the chosen part of a PRTG tree into Matmon elements under a target probe or folder: a PRTG probe and a group become
/// folders (only when something chosen lies inside), a device a host with its address, a sensor the mapped Matmon sensor
/// with PRTG's interval, tags and paused state. What exists already (same name in the same place) is reused, so running the
/// import twice creates nothing twice. The licence's sensor limit applies, as everywhere sensors are made.
/// </summary>
public sealed class PrtgImporter(IMonitoringWorkspaceStore store, Func<(bool Allowed, string? Reason)> canAddSensor)
{
    public async Task<PrtgImportReport> ImportAsync(PrtgClient client, IReadOnlyList<PrtgNode> tree, Guid targetParentId,
        IReadOnlySet<int> selectedSensorIds, bool probesAsFolders, CancellationToken cancellationToken)
    {
        var report = new PrtgImportReport();
        var children = store.GetAllElements()
            .Where(element => element.ParentId is not null)
            .GroupBy(element => element.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(element => (element.Id, element.Name, element.Kind)).ToList());

        // the properties the chosen sensors need, read up front (a few at a time - PRTG is not fast)
        var properties = await ReadPropertiesAsync(client, tree, selectedSensorIds, cancellationToken);

        foreach (var probe in tree)
        {
            var parent = probesAsFolders && Contains(probe, selectedSensorIds)
                ? Folder(targetParentId, probe.Name, probe.Tags, report, children)
                : targetParentId;
            ImportLevel(probe.Children, parent, probe.Name, selectedSensorIds, properties, report, children, deviceHost: null);
            if (report.LimitReason is not null)
            {
                break;
            }
        }
        return report;
    }

    private static bool Contains(PrtgNode node, IReadOnlySet<int> selected) =>
        node.Descendants().Any(child => child.Kind == PrtgNodeKind.Sensor && selected.Contains(child.Id));

    private void ImportLevel(IReadOnlyList<PrtgNode> nodes, Guid parentId, string path, IReadOnlySet<int> selected,
        IReadOnlyDictionary<int, Dictionary<string, string>> properties, PrtgImportReport report,
        Dictionary<Guid, List<(Guid Id, string Name, MonitoringElementKind Kind)>> children, string? deviceHost)
    {
        foreach (var node in nodes)
        {
            if (report.LimitReason is not null)
            {
                return;
            }
            var nodePath = $"{path} / {node.Name}";
            switch (node.Kind)
            {
                case PrtgNodeKind.Group or PrtgNodeKind.Probe:
                    if (Contains(node, selected))
                    {
                        var folder = Folder(parentId, node.Name, node.Tags, report, children);
                        ImportLevel(node.Children, folder, nodePath, selected, properties, report, children, deviceHost: null);
                    }
                    break;
                case PrtgNodeKind.Device:
                    if (Contains(node, selected))
                    {
                        var host = Host(parentId, node, report, children);
                        ImportLevel(node.Children, host, nodePath, selected, properties, report, children, node.Host);
                    }
                    break;
                case PrtgNodeKind.Sensor when selected.Contains(node.Id):
                    Sensor(parentId, node, nodePath, deviceHost, properties, report, children);
                    break;
            }
        }
    }

    private Guid Folder(Guid parentId, string name, IReadOnlyList<string> tags, PrtgImportReport report,
        Dictionary<Guid, List<(Guid Id, string Name, MonitoringElementKind Kind)>> children)
    {
        if (Existing(children, parentId, name, MonitoringElementKind.Folder) is Guid existing)
        {
            return existing;
        }
        var folder = store.CreateFolder(parentId, name, "Imported from PRTG");
        ApplyTags(folder.Id, tags);
        Remember(children, parentId, folder.Id, name, MonitoringElementKind.Folder);
        report.FoldersCreated++;
        return folder.Id;
    }

    private Guid Host(Guid parentId, PrtgNode device, PrtgImportReport report,
        Dictionary<Guid, List<(Guid Id, string Name, MonitoringElementKind Kind)>> children)
    {
        if (Existing(children, parentId, device.Name, MonitoringElementKind.Host) is Guid existing)
        {
            return existing;
        }
        var host = store.CreateHost(parentId, device.Name, device.Host ?? device.Name, $"Imported from PRTG (device #{device.Id})");
        ApplyTags(host.Id, device.Tags);
        Remember(children, parentId, host.Id, device.Name, MonitoringElementKind.Host);
        report.HostsCreated++;
        return host.Id;
    }

    private void Sensor(Guid parentId, PrtgNode sensor, string path, string? deviceHost,
        IReadOnlyDictionary<int, Dictionary<string, string>> properties, PrtgImportReport report,
        Dictionary<Guid, List<(Guid Id, string Name, MonitoringElementKind Kind)>> children)
    {
        var mapping = PrtgSensorMapping.Map(sensor.SensorKind, sensor.SensorType);
        if (mapping.SensorTypeKey is null)
        {
            report.NotImported.Add((path, mapping.Note));
            return;
        }
        if (Existing(children, parentId, sensor.Name, MonitoringElementKind.Sensor) is not null)
        {
            report.SensorsSkippedExisting++;
            return;
        }
        var (allowed, reason) = canAddSensor();
        if (!allowed)
        {
            report.LimitReason = reason;
            return;
        }

        var (target, parameters, missing) = PrtgSensorMapping.Settings(mapping.SensorTypeKey,
            properties.TryGetValue(sensor.Id, out var props) ? props : new Dictionary<string, string>(), deviceHost);
        var settings = new MonitoringSettings();
        foreach (var (key, value) in parameters)
        {
            settings.Parameters[key] = value;
        }
        if (sensor.IntervalSeconds is int seconds)
        {
            settings.PollingInterval = TimeSpan.FromSeconds(Math.Max(seconds, (int)SensorScheduleDefaults.Minimum.TotalSeconds));
        }

        var created = store.CreateSensor(parentId, sensor.Name, mapping.SensorTypeKey, target,
            $"Imported from PRTG (sensor #{sensor.Id}, {sensor.SensorType ?? sensor.SensorKind})", settings);
        ApplyTags(created.Id, sensor.Tags);
        if (sensor.Paused)
        {
            store.SetElementPaused(created.Id, true);
        }
        Remember(children, parentId, created.Id, sensor.Name, MonitoringElementKind.Sensor);
        report.SensorsCreated++;
        if (missing is not null || mapping.Level == PrtgMappingLevel.Partial)
        {
            report.ToComplete.Add((path, missing ?? mapping.Note));
        }
    }

    private void ApplyTags(Guid id, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }
        store.UpdateElement(id, element => element.Tags = MonitoringTagResolver.Normalize(element.Tags.Concat(tags)).ToList());
    }

    private static Guid? Existing(Dictionary<Guid, List<(Guid Id, string Name, MonitoringElementKind Kind)>> children, Guid parentId, string name, MonitoringElementKind kind) =>
        children.TryGetValue(parentId, out var list)
            ? list.Where(child => child.Kind == kind && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)).Select(child => (Guid?)child.Id).FirstOrDefault()
            : null;

    private static void Remember(Dictionary<Guid, List<(Guid Id, string Name, MonitoringElementKind Kind)>> children, Guid parentId, Guid id, string name, MonitoringElementKind kind)
    {
        if (!children.TryGetValue(parentId, out var list))
        {
            children[parentId] = list = [];
        }
        list.Add((id, name, kind));
    }

    private static async Task<IReadOnlyDictionary<int, Dictionary<string, string>>> ReadPropertiesAsync(PrtgClient client, IReadOnlyList<PrtgNode> tree,
        IReadOnlySet<int> selected, CancellationToken cancellationToken)
    {
        var wanted = tree.SelectMany(node => node.Descendants())
            .Where(node => node.Kind == PrtgNodeKind.Sensor && selected.Contains(node.Id))
            .Select(node => (node.Id, PrtgSensorMapping.Map(node.SensorKind, node.SensorType).Properties))
            .Where(item => item.Properties.Count > 0)
            .ToList();
        var result = new ConcurrentDictionary<int, Dictionary<string, string>>();
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, token) =>
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in item.Properties)
            {
                if (await client.GetPropertyAsync(item.Id, name, token) is { } value)
                {
                    values[name] = value;
                }
            }
            result[item.Id] = values;
        });
        return result;
    }
}
