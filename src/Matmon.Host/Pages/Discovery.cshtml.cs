using System.Globalization;
using System.Text.Json;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

[Authorize]
public sealed class DiscoveryModel : PageModel
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly NetworkDiscoveryService _discoveryService;
    private readonly DiscoveryJobStore _discoveryJobs;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public DiscoveryModel(
        IMonitoringWorkspaceStore workspaceStore,
        NetworkDiscoveryService discoveryService,
        DiscoveryJobStore discoveryJobs)
    {
        _workspaceStore = workspaceStore;
        _discoveryService = discoveryService;
        _discoveryJobs = discoveryJobs;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? JobId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Import { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    [BindProperty]
    public DiscoveryInput Input { get; set; } = new();

    [BindProperty]
    public List<DiscoveryResultInput> Results { get; set; } = [];

    [BindProperty]
    public Guid ImportJobId { get; set; }

    [BindProperty]
    public List<string> SelectedHostAddresses { get; set; } = [];

    [BindProperty]
    public List<string> SelectedSuggestionKeys { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public DiscoveryPageViewModel View { get; private set; } = default!;

    public IActionResult OnGet()
    {
        LoadView();
        return Page();
    }

    public IActionResult OnPostScan()
    {
        try
        {
            var probe = ResolveProbe(Input.ProbeElementId);
            var request = new NetworkDiscoveryRequest(Guid.NewGuid(), Input.Network, BuildOptions(Input));
            var job = _discoveryJobs.Create(probe.Id, probe.ProbeId, probe.Name, request);

            if (probe.ParentId is null)
            {
                StartLocalDiscovery(job);
                StatusMessage = $"Discovery job started on '{probe.Name}'.";
                return RedirectToPage(new { jobId = job.JobId, tab = "running" });
            }

            StatusMessage = $"Discovery job queued for probe '{probe.Name}'. The secondary probe will pick it up on its next sync.";
            return RedirectToPage(new { jobId = job.JobId, tab = "running" });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadView();
            return Page();
        }
    }

    public IActionResult OnPostScope(Guid scopeElementId, string? returnUrl)
    {
        try
        {
            var (probe, scope, hosts) = ResolveDiscoveryScope(scopeElementId);
            var network = string.Join(", ", hosts.Select(host => host.Address.Trim()));
            var effectiveSettings = ResolveEffectiveSettings(scope);
            var request = new NetworkDiscoveryRequest(
                Guid.NewGuid(),
                network,
                BuildScopeOptions(effectiveSettings, hosts.Count));
            var job = _discoveryJobs.Create(probe.Id, probe.ProbeId, probe.Name, request);
            var scopeLabel = scope is HostElement ? "host" : scope.Kind.ToString().ToLowerInvariant();

            if (probe.ParentId is null)
            {
                StartLocalDiscovery(job);
                StatusMessage = $"Discovery job started for {hosts.Count} host{(hosts.Count == 1 ? string.Empty : "s")} below {scopeLabel} '{scope.Name}'.";
                return RedirectToPage(new { jobId = job.JobId, tab = "running" });
            }

            StatusMessage = $"Discovery job queued on probe '{probe.Name}' for {hosts.Count} host{(hosts.Count == 1 ? string.Empty : "s")} below {scopeLabel} '{scope.Name}'.";
            return RedirectToPage(new { jobId = job.JobId, tab = "running" });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToPage("/Discovery");
        }
    }

    public IActionResult OnPostCancel(Guid jobId)
    {
        if (_discoveryJobs.Cancel(jobId))
        {
            StatusMessage = "Discovery job cancelled.";
        }
        else
        {
            ErrorMessage = "Discovery job could not be cancelled. It may already be finished.";
        }

        return RedirectToPage(new { jobId, tab = "history" });
    }

    public IActionResult OnPostCreateSelected()
    {
        try
        {
            var job = ImportJobId == Guid.Empty ? null : _discoveryJobs.Find(ImportJobId);
            var probe = job is not null
                ? ResolveProbe(job.ProbeElementId)
                : ResolveProbe(Input.ProbeElementId);
            var importInput = Input;
            var selected = job is not null
                ? BuildSelectedResultsFromJob(job, out importInput)
                : Results.Where(result => result.Selected).ToArray();
            if (selected.Length == 0)
            {
                throw new InvalidOperationException("No discovered hosts selected.");
            }

            var createdHosts = 0;
            var reusedHosts = 0;
            var createdSensors = 0;

            foreach (var result in selected)
            {
                var host = FindHostByAddress(probe, result.Address);
                if (host is null)
                {
                    host = _workspaceStore.CreateHost(
                        probe.Id,
                        BuildUniqueHostName(probe, result),
                        result.Address,
                        $"Discovered by auto discovery on {DateTimeOffset.Now:dd.MM.yyyy HH:mm}");
                    createdHosts++;
                }
                else
                {
                    reusedHosts++;
                }

                createdSensors += EnsureSelectedSensors(host, result, importInput);
            }

            StatusMessage = $"Discovery import done: {createdHosts} host{(createdHosts == 1 ? string.Empty : "s")} created, {reusedHosts} reused, {createdSensors} sensor{(createdSensors == 1 ? string.Empty : "s")} added.";
            return RedirectToPage("/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadView();
            return Page();
        }
    }

    private void LoadView()
    {
        var probes = _workspaceStore.GetAllElements().OfType<ProbeElement>().ToArray();
        if (Input.ProbeElementId == Guid.Empty)
        {
            Input.ProbeElementId = probes.FirstOrDefault(probe => probe.ParentId is null)?.Id
                ?? probes.FirstOrDefault()?.Id
                ?? Guid.Empty;
        }

        DiscoveryJobSnapshot? job = null;
        if (JobId is Guid jobId)
        {
            job = _discoveryJobs.Find(jobId);
            if (job is not null)
            {
                Input.ProbeElementId = job.ProbeElementId;
                Input.Network = job.Request.Network;
                ApplyOptionsToInput(job.Request.Options, Input);
                Results = job.Results.Select(ToInput).ToList();
                ImportJobId = job.JobId;
            }
        }

        var recentJobs = _discoveryJobs.GetRecent();
        var runningJobs = recentJobs
            .Where(job => job.Status is DiscoveryJobStatus.Pending or DiscoveryJobStatus.Running)
            .ToArray();
        var historyJobs = recentJobs
            .Where(job => job.Status is not DiscoveryJobStatus.Pending and not DiscoveryJobStatus.Running)
            .ToArray();
        var activeTab = ResolveActiveTab(job);
        Tab = activeTab;

        View = new DiscoveryPageViewModel(
            probes.Select(probe => new SelectListItem(
                $"{probe.Name} ({probe.ProbeId})",
                probe.Id.ToString(),
                probe.Id == Input.ProbeElementId)).ToArray(),
            recentJobs,
            runningJobs,
            historyJobs,
            job,
            activeTab);
    }

    private string ResolveActiveTab(DiscoveryJobSnapshot? job)
    {
        var requestedTab = NormalizeTab(Tab);
        if (requestedTab is not null)
        {
            return requestedTab;
        }

        if (job is not null)
        {
            return job.Status is DiscoveryJobStatus.Pending or DiscoveryJobStatus.Running
                ? "running"
                : "history";
        }

        return "scan";
    }

    public bool IsActiveTab(string tab)
    {
        return string.Equals(View.ActiveTab, tab, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeTab(string? tab)
    {
        return tab?.Trim().ToLowerInvariant() switch
        {
            "scan" => "scan",
            "running" => "running",
            "history" => "history",
            _ => null
        };
    }

    private DiscoveryResultInput[] BuildSelectedResultsFromJob(
        DiscoveryJobSnapshot job,
        out DiscoveryInput importInput)
    {
        importInput = new DiscoveryInput
        {
            ProbeElementId = job.ProbeElementId,
            Network = job.Request.Network
        };
        ApplyOptionsToInput(job.Request.Options, importInput);

        var selectedAddresses = SelectedHostAddresses
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedAddresses.Count == 0)
        {
            return [];
        }

        var selectedSuggestions = SelectedSuggestionKeys
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);

        return job.Results
            .Select(ToInput)
            .Where(result => selectedAddresses.Contains(result.Address))
            .Select(result =>
            {
                result.Selected = true;
                foreach (var suggestion in result.SuggestedSensors)
                {
                    suggestion.Selected = selectedSuggestions.Contains(BuildSuggestionKey(result.Address, suggestion));
                }

                return result;
            })
            .ToArray();
    }

    private ProbeElement ResolveProbe(Guid probeElementId)
    {
        if (probeElementId == Guid.Empty)
        {
            return _workspaceStore.Workspace.RootProbe;
        }

        return _workspaceStore.FindElement(probeElementId) as ProbeElement
            ?? throw new InvalidOperationException("Selected probe was not found.");
    }

    private (ProbeElement Probe, MonitoringElement Scope, IReadOnlyList<HostElement> Hosts) ResolveDiscoveryScope(Guid scopeElementId)
    {
        if (scopeElementId == Guid.Empty)
        {
            throw new InvalidOperationException("No discovery scope selected.");
        }

        var scope = _workspaceStore.FindElement(scopeElementId)
            ?? throw new InvalidOperationException("Selected discovery scope was not found.");
        if (scope is not ProbeElement and not FolderElement and not HostElement)
        {
            throw new InvalidOperationException("Discovery can be started from a probe, folder or host.");
        }

        var probe = scope as ProbeElement ?? ResolveOwningProbe(scope);
        var hosts = Enumerate(scope)
            .OfType<HostElement>()
            .Where(host => !string.IsNullOrWhiteSpace(host.Address))
            .GroupBy(host => host.Address.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (hosts.Length == 0)
        {
            throw new InvalidOperationException($"No hosts with an address were found below '{scope.Name}'.");
        }

        return (probe, scope, hosts);
    }

    private ProbeElement ResolveOwningProbe(MonitoringElement element)
    {
        var current = element;
        while (true)
        {
            if (current is ProbeElement probe)
            {
                return probe;
            }

            if (current.ParentId is not Guid parentId)
            {
                throw new InvalidOperationException($"No probe parent was found for '{element.Name}'.");
            }

            current = _workspaceStore.FindElement(parentId)
                ?? throw new InvalidOperationException($"Parent element '{parentId}' could not be found.");
        }
    }

    private MonitoringSettings ResolveEffectiveSettings(MonitoringElement element)
    {
        var snapshot = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(candidate => candidate.Id);
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var lineage = BuildLineage(element, elementsById);
        return _resolver.Resolve(lineage, templateMap);
    }

    private static IReadOnlyList<MonitoringElement> BuildLineage(
        MonitoringElement element,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);
            if (current.ParentId is not Guid parentId ||
                !elementsById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    private void StartLocalDiscovery(DiscoveryJobSnapshot job)
    {
        _ = Task.Run(async () =>
        {
            var cancellationToken = _discoveryJobs.GetCancellationToken(job.JobId);

            try
            {
                _discoveryJobs.Start(job.JobId, "Discovery is running on the primary probe.");
                await _discoveryService.DiscoverAsync(
                    job.Request,
                    (result, _) =>
                    {
                        _discoveryJobs.AddResult(job.JobId, result);
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken,
                    (progress, _) =>
                    {
                        _discoveryJobs.UpdateProgress(job.JobId, progress.ScannedHosts, progress.TotalHosts);
                        return ValueTask.CompletedTask;
                    });

                if (!_discoveryJobs.IsCancelled(job.JobId))
                {
                    _discoveryJobs.Complete(job.JobId, [], null);
                }
            }
            catch (OperationCanceledException) when (_discoveryJobs.IsCancelled(job.JobId))
            {
                // The user intentionally cancelled the job from the UI.
            }
            catch (Exception ex)
            {
                _discoveryJobs.Complete(job.JobId, [], ex.Message);
            }
        });
    }

    private static NetworkDiscoveryOptions BuildOptions(DiscoveryInput input)
    {
        return new NetworkDiscoveryOptions(
            input.UsePing,
            input.PingFirst,
            input.UseTcpPorts,
            ParsePorts(input.TcpPortsText),
            input.UseSnmp,
            input.SnmpCommunity,
            input.SnmpVersion,
            input.SnmpPort ?? 161,
            input.UseReverseDns,
            input.TimeoutMs ?? 650,
            input.MaxHosts ?? DiscoveryDefaults.Options.MaxHosts,
            input.Parallelism ?? 64).Normalized();
    }

    private static NetworkDiscoveryOptions BuildScopeOptions(MonitoringSettings settings, int hostCount)
    {
        var defaults = DiscoveryDefaults.Options;
        var snmpCommunity = MonitoringSettings.TryReadParameter(settings, "snmp.community", out var configuredCommunity)
            ? configuredCommunity
            : defaults.SnmpCommunity;
        var snmpVersion = MonitoringSettings.TryReadParameter(settings, "snmp.version", out var configuredVersion)
            ? configuredVersion
            : defaults.SnmpVersion;
        var snmpPort = MonitoringSettings.TryReadParameterInt(settings, "snmp.port", out var configuredSnmpPort)
            ? configuredSnmpPort
            : defaults.SnmpPort;
        var timeoutMs = settings.Timeout.HasValue
            ? (int)Math.Clamp(settings.Timeout.Value.TotalMilliseconds, 150, 10_000)
            : defaults.TimeoutMs;

        return new NetworkDiscoveryOptions(
            UsePing: true,
            PingFirst: false,
            UseTcpPorts: true,
            TcpPorts: defaults.TcpPorts,
            UseSnmp: true,
            SnmpCommunity: snmpCommunity,
            SnmpVersion: snmpVersion,
            SnmpPort: snmpPort,
            UseReverseDns: true,
            TimeoutMs: timeoutMs,
            MaxHosts: Math.Max(hostCount, 1),
            Parallelism: Math.Min(Math.Max(hostCount, 1), defaults.Parallelism)).Normalized();
    }

    private static void ApplyOptionsToInput(NetworkDiscoveryOptions options, DiscoveryInput input)
    {
        var normalized = options.Normalized();
        input.UsePing = normalized.UsePing;
        input.PingFirst = normalized.PingFirst;
        input.UseTcpPorts = normalized.UseTcpPorts;
        input.TcpPortsText = string.Join(", ", normalized.TcpPorts);
        input.UseSnmp = normalized.UseSnmp;
        input.SnmpCommunity = normalized.SnmpCommunity;
        input.SnmpVersion = normalized.SnmpVersion;
        input.SnmpPort = normalized.SnmpPort;
        input.UseReverseDns = normalized.UseReverseDns;
        input.TimeoutMs = normalized.TimeoutMs;
        input.MaxHosts = normalized.MaxHosts;
        input.Parallelism = normalized.Parallelism;
    }

    private int EnsureSelectedSensors(HostElement host, DiscoveryResultInput result, DiscoveryInput input)
    {
        var created = 0;
        var suggestions = result.SuggestedSensors.Count > 0
            ? result.SuggestedSensors
            : BuildFallbackSuggestions(result, input);

        foreach (var suggestion in suggestions.Where(suggestion => suggestion.Selected))
        {
            if (string.IsNullOrWhiteSpace(suggestion.SensorTypeKey))
            {
                continue;
            }

            var settings = DeserializeSettings(suggestion.SettingsJson);
            if (HasSuggestedSensor(host, suggestion, settings))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(suggestion.Name)
                ? suggestion.SensorTypeKey
                : suggestion.Name.Trim();
            var description = string.IsNullOrWhiteSpace(suggestion.Reason)
                ? "Created by auto discovery"
                : $"Created by auto discovery: {suggestion.Reason.Trim()}";
            _workspaceStore.CreateSensor(
                host.Id,
                name,
                suggestion.SensorTypeKey,
                suggestion.Target ?? string.Empty,
                description,
                settings);
            created++;
        }

        return created;
    }

    private static IReadOnlyList<int> ParsePorts(string? rawPorts)
    {
        if (string.IsNullOrWhiteSpace(rawPorts))
        {
            return [];
        }

        return rawPorts
            .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 0)
            .Where(port => port is >= 1 and <= 65535)
            .Distinct()
            .Take(32)
            .ToArray();
    }

    private static DiscoveryResultInput ToInput(NetworkDiscoveryResult result)
    {
        return new DiscoveryResultInput
        {
            Selected = true,
            Address = result.Address,
            HostName = result.HostName,
            PingAlive = result.PingAlive,
            PingMs = result.PingMs,
            OpenPortsText = string.Join(", ", result.OpenPorts),
            SnmpResponded = result.SnmpResponded,
            SnmpSummary = result.SnmpSummary,
            Message = result.Message,
            SuggestedSensors = result.SuggestedSensors.Select(ToInput).ToList()
        };
    }

    private static DiscoverySensorSuggestionInput ToInput(SensorDiscoverySuggestion suggestion)
    {
        return new DiscoverySensorSuggestionInput
        {
            Selected = true,
            SensorTypeKey = suggestion.SensorTypeKey,
            Name = suggestion.Name,
            Target = suggestion.Target,
            Reason = suggestion.Reason,
            Confidence = suggestion.Confidence,
            SettingsJson = JsonSerializer.Serialize(suggestion.Settings, SettingsJsonOptions)
        };
    }

    private static string BuildSuggestionKey(string address, DiscoverySensorSuggestionInput suggestion)
    {
        return $"{address}|{suggestion.SensorTypeKey}|{suggestion.Target}|{suggestion.Name}";
    }

    private static HostElement? FindHostByAddress(ProbeElement probe, string address)
    {
        return Enumerate(probe)
            .OfType<HostElement>()
            .FirstOrDefault(host => string.Equals(host.Address, address, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildUniqueHostName(ProbeElement probe, DiscoveryResultInput result)
    {
        var baseName = string.IsNullOrWhiteSpace(result.HostName)
            ? result.Address
            : result.HostName.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        var existingNames = Enumerate(probe)
            .Select(element => element.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = baseName;
        var suffix = 2;
        while (existingNames.Contains(candidate))
        {
            candidate = $"{baseName} {suffix++}";
        }

        return candidate;
    }

    private static bool HasSuggestedSensor(
        HostElement host,
        DiscoverySensorSuggestionInput suggestion,
        MonitoringSettings settings)
    {
        return host.Children.OfType<SensorElement>().Any(sensor =>
            string.Equals(sensor.SensorTypeKey, suggestion.SensorTypeKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sensor.Target ?? string.Empty, suggestion.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            SuggestedSettingsMatch(sensor.Settings, settings));
    }

    private static bool SuggestedSettingsMatch(MonitoringSettings existingSettings, MonitoringSettings suggestedSettings)
    {
        var identityKeys = new[]
        {
            "tcp.port",
            "ssl.port",
            "mssql.port",
            "pve.port",
            "pve.scope",
            "snmp.oids",
            "winrm.port",
            "query"
        };
        var relevantKeys = identityKeys
            .Where(key =>
                suggestedSettings.Parameters.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return relevantKeys.Length == 0 ||
            relevantKeys.All(key =>
                existingSettings.Parameters.TryGetValue(key, out var existingValue) &&
                string.Equals(existingValue, suggestedSettings.Parameters[key], StringComparison.Ordinal));
    }

    private static MonitoringSettings DeserializeSettings(string? rawSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(rawSettingsJson))
        {
            return new MonitoringSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<MonitoringSettings>(rawSettingsJson, SettingsJsonOptions) ?? new MonitoringSettings();
        }
        catch
        {
            return new MonitoringSettings();
        }
    }

    private static List<DiscoverySensorSuggestionInput> BuildFallbackSuggestions(
        DiscoveryResultInput result,
        DiscoveryInput input)
    {
        var suggestions = new List<DiscoverySensorSuggestionInput>();
        var ports = ParsePorts(result.OpenPortsText);

        if (input.CreatePingSensor && result.PingAlive)
        {
            suggestions.Add(ToInput(new SensorDiscoverySuggestion(
                PingSensorExecutor.Definition.Key,
                "Ping",
                string.Empty,
                new MonitoringSettings(),
                "Ping answered.",
                95)));
        }

        if (input.CreatePortSensors)
        {
            foreach (var port in ports)
            {
                var settings = new MonitoringSettings();
                settings.Parameters["tcp.port"] = port.ToString(CultureInfo.InvariantCulture);
                settings.Parameters["tcp.expectedOpen"] = "true";
                suggestions.Add(ToInput(new SensorDiscoverySuggestion(
                    TcpPortSensorExecutor.Definition.Key,
                    $"Port {port}",
                    string.Empty,
                    settings,
                    $"TCP port {port} is open.",
                    85)));
            }
        }

        if (input.CreateHttpSensors)
        {
            if (ports.Contains(80))
            {
                suggestions.Add(ToInput(BuildHttpFallbackSuggestion(result.Address, useSsl: false)));
            }

            if (ports.Contains(443))
            {
                suggestions.Add(ToInput(BuildHttpFallbackSuggestion(result.Address, useSsl: true)));
            }
        }

        if (input.CreateSnmpSensor && result.SnmpResponded)
        {
            var settings = new MonitoringSettings();
            settings.Parameters["snmp.community"] = string.IsNullOrWhiteSpace(input.SnmpCommunity) ? "public" : input.SnmpCommunity.Trim();
            settings.Parameters["snmp.version"] = input.SnmpVersion is "v1" ? "v1" : "v2c";
            settings.Parameters["snmp.port"] = (input.SnmpPort ?? 161).ToString(CultureInfo.InvariantCulture);
            settings.Parameters["snmp.oids"] = "1.3.6.1.2.1.1.3.0|Uptime";
            suggestions.Add(ToInput(new SensorDiscoverySuggestion(
                SnmpSensorExecutor.Definition.Key,
                "SNMP Uptime",
                string.Empty,
                settings,
                "SNMP answered.",
                90)));
        }

        return suggestions;
    }

    private static SensorDiscoverySuggestion BuildHttpFallbackSuggestion(string address, bool useSsl)
    {
        var settings = new MonitoringSettings();
        settings.Parameters["method"] = "HEAD";
        settings.Parameters["expectedStatus"] = "200";
        var scheme = useSsl ? "https" : "http";

        return new SensorDiscoverySuggestion(
            HttpSensorExecutor.Definition.Key,
            useSsl ? "HTTPS" : "HTTP",
            $"{scheme}://{address}/",
            settings,
            $"TCP port {(useSsl ? 443 : 80)} is open.",
            82);
    }

    private static IEnumerable<MonitoringElement> Enumerate(MonitoringElement element)
    {
        yield return element;

        if (element is not MonitoringContainerElement container)
        {
            yield break;
        }

        foreach (var child in container.Children)
        {
            foreach (var descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }
}

public sealed class DiscoveryInput
{
    public Guid ProbeElementId { get; set; }

    public string Network { get; set; } = string.Empty;

    public bool UsePing { get; set; } = true;

    public bool PingFirst { get; set; }

    public bool UseTcpPorts { get; set; } = true;

    public string TcpPortsText { get; set; } = "22, 80, 135, 139, 443, 445, 1433, 3389, 5000, 5001, 5985, 5986, 8006, 8080, 8099, 8443";

    public bool UseSnmp { get; set; }

    public string SnmpCommunity { get; set; } = "public";

    public string SnmpVersion { get; set; } = "v2c";

    public int? SnmpPort { get; set; } = 161;

    public bool UseReverseDns { get; set; } = true;

    public int? TimeoutMs { get; set; } = 650;

    public int? MaxHosts { get; set; } = 65_534;

    public int? Parallelism { get; set; } = 64;

    public bool CreatePingSensor { get; set; } = true;

    public bool CreatePortSensors { get; set; } = true;

    public bool CreateHttpSensors { get; set; } = true;

    public bool CreateSnmpSensor { get; set; }
}

public sealed class DiscoveryResultInput
{
    public bool Selected { get; set; }

    public string Address { get; set; } = string.Empty;

    public string? HostName { get; set; }

    public bool PingAlive { get; set; }

    public double? PingMs { get; set; }

    public string OpenPortsText { get; set; } = string.Empty;

    public bool SnmpResponded { get; set; }

    public string? SnmpSummary { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<DiscoverySensorSuggestionInput> SuggestedSensors { get; set; } = [];
}

public sealed class DiscoverySensorSuggestionInput
{
    public bool Selected { get; set; }

    public string SensorTypeKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public string SettingsJson { get; set; } = string.Empty;
}

public sealed record DiscoveryPageViewModel(
    IReadOnlyList<SelectListItem> ProbeOptions,
    IReadOnlyList<DiscoveryJobSnapshot> RecentJobs,
    IReadOnlyList<DiscoveryJobSnapshot> RunningJobs,
    IReadOnlyList<DiscoveryJobSnapshot> HistoryJobs,
    DiscoveryJobSnapshot? SelectedJob,
    string ActiveTab);
