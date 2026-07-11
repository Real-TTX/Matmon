using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private readonly IProbeRegistry _probeRegistry;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public DiscoveryModel(
        IMonitoringWorkspaceStore workspaceStore,
        NetworkDiscoveryService discoveryService,
        DiscoveryJobStore discoveryJobs,
        IProbeRegistry probeRegistry)
    {
        _workspaceStore = workspaceStore;
        _discoveryService = discoveryService;
        _discoveryJobs = discoveryJobs;
        _probeRegistry = probeRegistry;
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

    /// <summary>View mode (no more tab bar): "home" = Running section + the Jobs (history) list; "scan" = the
    /// start-a-scan form on its own screen (reached via the "Discover" button); "job" = a selected job's detail
    /// and results. Computed in <see cref="LoadView"/> from Tab / JobId / posted results.</summary>
    public string Mode { get; private set; } = "home";

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
            var network = ResolveScanNetwork(probe, Input);
            var request = new NetworkDiscoveryRequest(
                Guid.NewGuid(),
                network,
                BuildOptions(Input),
                ScopeElementId: probe.Id,
                ScopeKind: MonitoringElementKind.Probe);
            var job = _discoveryJobs.Create(probe.Id, probe.ProbeId, probe.Name, request);

            if (probe.ParentId is null)
            {
                StartLocalDiscovery(job);
                StatusMessage = $"Discovery job started on '{probe.Name}'.";
                return RedirectToPage(new { jobId = job.JobId });
            }

            StatusMessage = $"Discovery job queued for probe '{probe.Name}'. The secondary probe will pick it up on its next sync.";
            return RedirectToPage(new { jobId = job.JobId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Tab = "scan"; // keep the scan form on screen so the error is shown in context
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
                BuildScopeOptions(effectiveSettings, hosts.Count),
                ScopeElementId: scope.Id,
                ScopeKind: scope.Kind);
            var job = _discoveryJobs.Create(probe.Id, probe.ProbeId, probe.Name, request);
            var scopeLabel = scope is HostElement ? "host" : scope.Kind.ToString().ToLowerInvariant();

            if (probe.ParentId is null)
            {
                StartLocalDiscovery(job);
                StatusMessage = $"Discovery job started for {hosts.Count} host{(hosts.Count == 1 ? string.Empty : "s")} below {scopeLabel} '{scope.Name}'.";
                return RedirectToPage(new { jobId = job.JobId });
            }

            StatusMessage = $"Discovery job queued on probe '{probe.Name}' for {hosts.Count} host{(hosts.Count == 1 ? string.Empty : "s")} below {scopeLabel} '{scope.Name}'.";
            return RedirectToPage(new { jobId = job.JobId });
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

        return RedirectToPage("/Discovery");
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
            var hostScopeImport = job is not null &&
                job.Request.ScopeKind == MonitoringElementKind.Host &&
                job.Request.ScopeElementId is Guid;
            HostElement? scopeHost = null;
            if (hostScopeImport)
            {
                scopeHost = _workspaceStore.FindElement(job!.Request.ScopeElementId!.Value) as HostElement
                    ?? throw new InvalidOperationException("The selected host scope could not be resolved.");
            }

            foreach (var result in selected)
            {
                var host = scopeHost ?? FindHostByAddress(probe, result.Address);
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

        // No tab bar anymore: the "Discover" button opens the scan form (tab=scan) as its own screen;
        // a selected/just-posted job shows its detail+results; otherwise the Running + Jobs landing.
        Mode = string.Equals(Tab, "scan", StringComparison.OrdinalIgnoreCase)
            ? "scan"
            : (job is not null || Results.Count > 0) ? "job" : "home";

        var scopeProbe = probes.FirstOrDefault(probe => probe.Id == Input.ProbeElementId)
            ?? probes.FirstOrDefault(probe => probe.ParentId is null)
            ?? probes.FirstOrDefault();
        var knownHostAddresses = scopeProbe is null ? [] : GetKnownHostAddresses(scopeProbe);
        var probeReportedNetworks = ResolveProbeReportedNetworks(scopeProbe);
        var subnetSuggestions = BuildSubnetSuggestions(knownHostAddresses, probeReportedNetworks, includeLocal: scopeProbe?.ParentId is null);

        AnnotateExistingElements(job);

        View = new DiscoveryPageViewModel(
            probes.Select(probe => new SelectListItem(
                $"{probe.Name} ({probe.ProbeId})",
                probe.Id.ToString(),
                probe.Id == Input.ProbeElementId)).ToArray(),
            recentJobs,
            runningJobs,
            historyJobs,
            job,
            subnetSuggestions,
            knownHostAddresses.Count);
    }

    /// <summary>Mark discovered hosts/sensors that already exist under the import probe, so the create assistant can
    /// show that a host won't be recreated (only its missing sensors are added). Mirrors the exact reuse logic of
    /// <see cref="OnPostCreateSelected"/>: a host-scoped job attaches every result to its scope host; otherwise a
    /// host is matched by address. Display-only - it never changes what gets created.</summary>
    private void AnnotateExistingElements(DiscoveryJobSnapshot? job)
    {
        if (Results.Count == 0)
        {
            return;
        }

        ProbeElement probe;
        try
        {
            probe = ResolveProbe(Input.ProbeElementId);
        }
        catch (InvalidOperationException)
        {
            return; // probe gone - nothing to annotate against
        }

        var scopeHost = job is not null &&
            job.Request.ScopeKind == MonitoringElementKind.Host &&
            job.Request.ScopeElementId is Guid scopeId
                ? _workspaceStore.FindElement(scopeId) as HostElement
                : null;

        foreach (var result in Results)
        {
            var host = scopeHost ?? FindHostByAddress(probe, result.Address);
            if (host is null)
            {
                continue;
            }

            result.ExistingHostName = host.Name;
            foreach (var suggestion in result.SuggestedSensors)
            {
                suggestion.AlreadyExists = HasSuggestedSensor(host, suggestion, DeserializeSettings(suggestion.SettingsJson));
            }
        }
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
        if (selectedAddresses.Count == 0 &&
            job.Request.ScopeKind == MonitoringElementKind.Host &&
            job.Request.ScopeElementId.HasValue)
        {
            selectedAddresses = job.Results
                .Select(result => result.Address)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

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

    // Turns the chosen scan scope into a concrete network spec for the discovery
    // engine: the existing host addresses under the probe ("known"), or the
    // CIDR/range/address the user typed ("network", the default).
    private static string ResolveScanNetwork(ProbeElement probe, DiscoveryInput input)
    {
        if (string.Equals(input.ScanScope, "known", StringComparison.OrdinalIgnoreCase))
        {
            var addresses = GetKnownHostAddresses(probe);
            if (addresses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No hosts with an address exist under '{probe.Name}'. Add hosts first or scan a subnet.");
            }

            return string.Join(", ", addresses);
        }

        var network = (input.Network ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(network))
        {
            throw new InvalidOperationException(
                "Enter a subnet (CIDR), range or address to scan, or choose 'Known hosts'.");
        }

        return network;
    }

    private static IReadOnlyList<string> GetKnownHostAddresses(ProbeElement probe)
    {
        return Enumerate(probe)
            .OfType<HostElement>()
            .Where(host => !string.IsNullOrWhiteSpace(host.Address))
            .Select(host => host.Address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NetworkTargetParser.ToSortableAddress)
            .ToArray();
    }

    private IReadOnlyList<string> ResolveProbeReportedNetworks(ProbeElement? probe)
    {
        if (probe is null)
        {
            return [];
        }

        var snapshot = string.IsNullOrWhiteSpace(probe.ProbeId)
            ? null
            : _probeRegistry.GetAll()
                .FirstOrDefault(candidate => string.Equals(candidate.ProbeId, probe.ProbeId, StringComparison.OrdinalIgnoreCase));

        // Admin-configured scan subnets first, then the auto-detected ones the probe reported.
        return probe.Subnets
            .Concat(snapshot?.Networks ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildSubnetSuggestions(
        IReadOnlyList<string> knownAddresses,
        IReadOnlyList<string> probeReportedNetworks,
        bool includeLocal)
    {
        var subnets = new List<string>();

        void Add(string candidate)
        {
            if (!subnets.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                subnets.Add(candidate);
            }
        }

        // The subnets the (remote) probe actually reported are the most relevant - list them first.
        foreach (var cidr in probeReportedNetworks)
        {
            Add(cidr);
        }

        foreach (var address in knownAddresses)
        {
            if (TryDeriveSlash24(address, out var cidr))
            {
                Add(cidr);
            }
        }

        if (includeLocal)
        {
            foreach (var cidr in GetLocalSubnets())
            {
                Add(cidr);
            }
        }

        return subnets.Take(8).ToArray();
    }

    private static bool TryDeriveSlash24(string address, out string cidr)
    {
        cidr = string.Empty;
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        cidr = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        return true;
    }

    private static IEnumerable<string> GetLocalSubnets()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                var prefix = unicast.PrefixLength is > 0 and <= 32 ? unicast.PrefixLength : 24;
                var bytes = unicast.Address.GetAddressBytes();
                var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                var network = value & mask;
                yield return $"{(byte)(network >> 24)}.{(byte)(network >> 16)}.{(byte)(network >> 8)}.{(byte)network}/{prefix}";
            }
        }
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

    /// <summary>"network" = scan the typed CIDR/range/IP; "known" = re-scan existing hosts under the probe.</summary>
    public string ScanScope { get; set; } = "network";

    public string Network { get; set; } = string.Empty;

    public bool UsePing { get; set; } = true;

    public bool PingFirst { get; set; }

    public bool UseTcpPorts { get; set; } = true;

    public string TcpPortsText { get; set; } = "22, 80, 135, 139, 443, 445, 1433, 3389, 5000, 5001, 5985, 5986, 8006, 8080, 8099, 8443";

    public bool UseSnmp { get; set; } = true;

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

    /// <summary>Display-only (set in LoadView): the name of an already-existing host under the import probe with
    /// this address. Non-null = the host won't be recreated on import, only its missing sensors are added.</summary>
    public string? ExistingHostName { get; set; }

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

    /// <summary>Display-only (set in LoadView): this exact sensor already exists on the matched host, so import
    /// would skip it. Rendered with a green "Already added" check + not pre-selected.</summary>
    public bool AlreadyExists { get; set; }
}

public sealed record DiscoveryPageViewModel(
    IReadOnlyList<SelectListItem> ProbeOptions,
    IReadOnlyList<DiscoveryJobSnapshot> RecentJobs,
    IReadOnlyList<DiscoveryJobSnapshot> RunningJobs,
    IReadOnlyList<DiscoveryJobSnapshot> HistoryJobs,
    DiscoveryJobSnapshot? SelectedJob,
    IReadOnlyList<string> SubnetSuggestions,
    int KnownHostCount);
