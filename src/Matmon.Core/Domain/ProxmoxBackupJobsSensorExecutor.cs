using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Matmon.Core.Domain;

/// <summary>
/// The backup jobs of a Proxmox VE node or cluster (Datacenter &gt; Backup - vzdump, to any storage: a local directory, NFS,
/// a Proxmox Backup Server), read through the PVE API with the same API token as the other Proxmox sensors. One channel per
/// guest a job covers (and per guest that was backed up by hand in the window): the result of its LAST backup run (0 = done,
/// 1 = done with warnings / no run in the window, 2 = failed) in that guest's own state with the run's words as its message,
/// and the hours since its newest backup file. The worst guest decides the sensor; the message names every guest that is not
/// fine, and the guests that are in no enabled job.
/// <para>Where the numbers come from: <c>cluster/backup</c> (the jobs and what they cover - all guests with excludes, a list,
/// a pool, optionally one node), <c>cluster/resources?type=vm</c> (guest names), the vzdump tasks of every online node in the
/// window (<c>pvebackup.days</c>, default 8 - a weekly job is still seen) and their task LOGS, which say per guest
/// "Finished Backup of VM 100" or "Backup of VM 101 failed - ..."; the newest backup per guest from the content of every
/// backup storage. Needs the PVEAuditor role on / for the token (Sys.Audit for the jobs and tasks, Datastore.Audit for the
/// storages). Tested against a stand-in; the log lines follow vzdump's own wording - NOT verified against a real cluster.</para>
/// </summary>
public sealed partial class ProxmoxBackupJobsSensorExecutor : ISensorExecutor
{
    public const string FailedChannel = "failedGuests";
    public const string WarningChannel = "warningGuests";
    public const string WithoutJobChannel = "guestsWithoutJob";
    public const string OldestBackupChannel = "oldestBackupHours";

    /// <summary>At most this many guests get channels.</summary>
    public const int MaxGuestChannels = 150;

    /// <summary>At most this many task logs are read per check (newest first).</summary>
    public const int MaxTaskLogs = 40;

    private readonly Func<bool, HttpMessageHandler> _handlerFactory;

    public ProxmoxBackupJobsSensorExecutor()
        : this(DefaultHandler)
    {
    }

    /// <summary>For tests: the handler every request goes through (the flag is "verify the certificate").</summary>
    public ProxmoxBackupJobsSensorExecutor(Func<bool, HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "proxmox-backup-jobs",
        DisplayName = "Proxmox VE Backup Jobs",
        Description = "The backup jobs of a Proxmox VE node or cluster (vzdump, to any storage): one channel per guest with the result of its last backup and the hours since its newest backup, failed backups named, guests that are in no job.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            .. ProxmoxPveSensorExecutor.Definition.Parameters
                .Where(parameter => !string.Equals(parameter.Key, "pve.scope", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parameter.Key, "pve.node", StringComparison.OrdinalIgnoreCase)),
            new SensorParameterDefinition
            {
                Key = "pvebackup.days",
                Label = "Look back (days)",
                Group = "Backups",
                Kind = SensorParameterKind.Integer,
                Description = "How far back backup runs are read. A guest without a run in this window is a warning - raise it for a job that runs less often than weekly.",
                DefaultValue = "8",
                Min = 1,
                Max = 35,
                Step = "1"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (!context.OpenTcpPorts.Contains(8006))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings { DefaultChannelKey = FailedChannel };
        settings.Parameters["pve.port"] = "8006";
        settings.Parameters["pve.verifySsl"] = "false";
        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(new SensorDiscoverySuggestion(
            Definition.Key, "Proxmox VE Backup Jobs", string.Empty, settings,
            "Proxmox API port 8006 is open. API credentials can be inherited from the parent.", 60)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "PVE host / API URL is required");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "pve.user", out var user);
        user = (user ?? string.Empty).Trim();
        if (!MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenId", out var tokenId) || string.IsNullOrWhiteSpace(tokenId) ||
            !MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenSecret", out var tokenSecret) || string.IsNullOrWhiteSpace(tokenSecret))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Proxmox token ID and token secret are required");
        }

        if (!tokenId.Contains('!') && string.IsNullOrWhiteSpace(user))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Enter the full Token ID (user@realm!name), or set the API user separately");
        }

        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "pve.verifySsl", out var configuredVerify) && configuredVerify;
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "pve.port", out var configuredPort) ? configuredPort : 8006;
        var days = MonitoringSettings.TryReadParameterInt(context.Settings, "pvebackup.days", out var configuredDays) ? Math.Clamp(configuredDays, 1, 35) : 8;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(45);

        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(verifySsl), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new Api(client, ProxmoxPveSensorExecutor.BuildApiBaseUri(context.Target, port), ProxmoxPveSensorExecutor.BuildAuthorizationHeader(user, tokenId, tokenSecret));
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-days);
        var notes = new List<string>();
        try
        {
            var jobs = Items(await api.GetAsync("cluster/backup", "the backup jobs", timeoutCts.Token)).Select(ReadJob).ToList();
            var guests = Items(await api.GetAsync("cluster/resources?type=vm", "the guests", timeoutCts.Token))
                .Select(ReadGuest).Where(guest => guest is not null).Select(guest => guest!).ToList();
            var nodes = Items(await api.GetAsync("nodes", "the nodes", timeoutCts.Token))
                .Where(node => !string.Equals(Text(node, "status"), "offline", StringComparison.OrdinalIgnoreCase))
                .Select(node => Text(node, "node")).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList();

            // The vzdump tasks of the window, newest first, from every node.
            var tasks = new List<PveTask>();
            foreach (var node in nodes)
            {
                var list = await api.TryGetAsync(
                    $"nodes/{Uri.EscapeDataString(node)}/tasks?typefilter=vzdump&source=all&limit=500&since={since.ToUnixTimeSeconds()}", timeoutCts.Token);
                if (list is null)
                {
                    notes.Add($"the tasks of node {node} could not be read");
                    continue;
                }

                tasks.AddRange(Items(list.Value).Select(task => ReadTask(task, node)).Where(task => task is not null && task.Started >= since).Select(task => task!));
            }

            var runs = new List<GuestRun>();
            var runFailures = new List<string>();
            var running = 0;
            foreach (var task in tasks.OrderByDescending(task => task.Started).Take(MaxTaskLogs))
            {
                if (task.Status is null)
                {
                    running++;
                    continue;
                }

                var log = await api.TryGetAsync($"nodes/{Uri.EscapeDataString(task.Node)}/tasks/{Uri.EscapeDataString(task.Upid)}/log?start=0&limit=50000", timeoutCts.Token);
                var lines = log is null ? [] : Items(log.Value).Select(line => Text(line, "t") ?? string.Empty).ToList();
                var parsed = ParseLog(lines);
                runs.AddRange(parsed.Select(run => new GuestRun(run.VmId, task.Started, run.Result, run.Message, task.Node)));
                if (parsed.Count == 0 && !IsOk(task.Status))
                {
                    if (int.TryParse(task.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var vmid))
                    {
                        runs.Add(new GuestRun(vmid, task.Started, task.Status.StartsWith("WARNINGS", StringComparison.OrdinalIgnoreCase) ? 1 : 2, task.Status, task.Node));
                    }
                    else
                    {
                        runFailures.Add($"backup run on {task.Node} at {task.Started.ToLocalTime():g}: {task.Status}");
                    }
                }
            }

            var lastBackups = await ReadNewestBackupsAsync(api, nodes, notes, timeoutCts.Token);
            watch.Stop();
            var result = BuildResult(jobs, guests, runs, lastBackups, runFailures, running, notes, days, now, watch.Elapsed);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, "request timed out - raise the sensor's timeout for a cluster with many backups");
        }
        catch (OperationCanceledException)
        {
            return SensorExecutionResult.Unknown("execution cancelled");
        }
        catch (Exception ex)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    /// <summary>The newest backup file per guest across every backup storage (a shared one - NFS, PBS - is read once).</summary>
    private static async Task<Dictionary<int, DateTimeOffset>> ReadNewestBackupsAsync(Api api, IReadOnlyList<string> nodes, List<string> notes, CancellationToken cancellationToken)
    {
        var newest = new Dictionary<int, DateTimeOffset>();
        var sharedDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var storages = await api.TryGetAsync($"nodes/{Uri.EscapeDataString(node)}/storage?content=backup&enabled=1", cancellationToken);
            foreach (var storage in storages is null ? [] : Items(storages.Value))
            {
                var name = Text(storage, "storage");
                if (string.IsNullOrWhiteSpace(name) || Number(storage, "active") == 0)
                {
                    continue;
                }

                if (Number(storage, "shared") == 1 && !sharedDone.Add(name))
                {
                    continue;
                }

                var content = await api.TryGetAsync($"nodes/{Uri.EscapeDataString(node)}/storage/{Uri.EscapeDataString(name)}/content?content=backup", cancellationToken);
                if (content is null)
                {
                    notes.Add($"storage {name} could not be read");
                    continue;
                }

                foreach (var file in Items(content.Value))
                {
                    if (Number(file, "vmid") is not { } vmid || Number(file, "ctime") is not { } ctime)
                    {
                        continue;
                    }

                    var at = DateTimeOffset.FromUnixTimeSeconds((long)ctime);
                    if (!newest.TryGetValue((int)vmid, out var known) || at > known)
                    {
                        newest[(int)vmid] = at;
                    }
                }
            }
        }

        return newest;
    }

    public sealed record PveBackupJob(string Id, bool Enabled, bool All, IReadOnlyList<int> VmIds, IReadOnlyList<int> Exclude, string? Pool, string? Node, string? Storage, string? Schedule, string? Comment);

    public sealed record PveGuest(int VmId, string Type, string? Name, string? Node, string? Pool, bool Template);

    public sealed record PveTask(string Upid, string Node, string? Id, DateTimeOffset Started, string? Status);

    /// <summary>One guest in one backup run: 0 = done, 1 = done with warnings, 2 = failed.</summary>
    public sealed record GuestRun(int VmId, DateTimeOffset Started, int Result, string? Message, string Node);

    public sealed record LogRun(int VmId, int Result, string? Message);

    /// <summary>Does the job back the guest up? All guests (minus its excludes), a list, a pool - and only on its node when it names one.</summary>
    public static bool Covers(PveBackupJob job, PveGuest guest)
    {
        if (!job.Enabled || (job.Node is { Length: > 0 } node && !string.Equals(node, guest.Node, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (job.All)
        {
            return !job.Exclude.Contains(guest.VmId);
        }

        return job.VmIds.Contains(guest.VmId)
            || (job.Pool is { Length: > 0 } pool && string.Equals(pool, guest.Pool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a vzdump task log says per guest: "INFO: Starting Backup of VM 100 (qemu)", "WARN: ..." while it runs,
    /// "INFO: Finished Backup of VM 100 (00:01:02)" or "ERROR: Backup of VM 101 failed - &lt;reason&gt;". A guest that started and
    /// never finished nor failed (the task was aborted) counts as failed.
    /// </summary>
    public static IReadOnlyList<LogRun> ParseLog(IEnumerable<string> lines)
    {
        var results = new List<LogRun>();
        int? current = null;
        string? firstWarning = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (StartRegex().Match(line) is { Success: true } start)
            {
                if (current is { } open)
                {
                    results.Add(new LogRun(open, 2, "the backup did not finish"));
                }

                current = int.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture);
                firstWarning = null;
                continue;
            }

            if (FailRegex().Match(line) is { Success: true } fail)
            {
                var vmid = int.Parse(fail.Groups[1].Value, CultureInfo.InvariantCulture);
                results.Add(new LogRun(vmid, 2, fail.Groups[2].Value.Trim()));
                if (current == vmid)
                {
                    current = null;
                }

                continue;
            }

            if (FinishRegex().Match(line) is { Success: true } finish)
            {
                var vmid = int.Parse(finish.Groups[1].Value, CultureInfo.InvariantCulture);
                var took = finish.Groups[2].Success ? finish.Groups[2].Value : null;
                results.Add(firstWarning is not null && current == vmid
                    ? new LogRun(vmid, 1, firstWarning)
                    : new LogRun(vmid, 0, took is null ? "done" : $"done in {took}"));
                current = null;
                firstWarning = null;
                continue;
            }

            if (current is not null && firstWarning is null && (line.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)))
            {
                firstWarning = line[(line.IndexOf(':') + 1)..].Trim();
            }
        }

        if (current is { } unfinished)
        {
            results.Add(new LogRun(unfinished, 2, "the backup did not finish"));
        }

        return results;
    }

    public static SensorExecutionResult BuildResult(
        IReadOnlyList<PveBackupJob> jobs,
        IReadOnlyList<PveGuest> guests,
        IReadOnlyList<GuestRun> runs,
        IReadOnlyDictionary<int, DateTimeOffset> newestBackups,
        IReadOnlyList<string> runFailures,
        int running,
        IReadOnlyList<string> notes,
        int days,
        DateTimeOffset now,
        TimeSpan duration)
    {
        var enabledJobs = jobs.Count(job => job.Enabled);
        if (jobs.Count == 0)
        {
            return SensorExecutionResult.Warning(duration, "No backup job is set up on this cluster (Datacenter > Backup) - or the API token lacks Sys.Audit on / (give it the PVEAuditor role).");
        }

        var byId = guests.GroupBy(guest => guest.VmId).ToDictionary(group => group.Key, group => group.First());
        var covered = guests.Where(guest => jobs.Any(job => Covers(job, guest))).Select(guest => guest.VmId).ToHashSet();
        var lastRun = runs.GroupBy(run => run.VmId).ToDictionary(group => group.Key, group => group.OrderByDescending(run => run.Started).First());

        // Channels for the guests a job covers, and for any guest backed up in the window (a manual backup, a removed job).
        var watched = covered.Union(lastRun.Keys)
            .OrderBy(vmid => vmid)
            .Take(MaxGuestChannels)
            .ToList();

        var channels = new List<SensorChannelValue>();
        var failed = new List<string>();
        var warned = new List<string>();
        var ages = new List<double>();
        foreach (var vmid in watched)
        {
            byId.TryGetValue(vmid, out var guest);
            var kind = guest?.Type == "lxc" ? "ct" : "vm";
            var label = guest?.Name is { Length: > 0 } name ? $"{name} ({kind.ToUpperInvariant()} {vmid})" : $"{kind.ToUpperInvariant()} {vmid}";
            int code;
            string message;
            var run = lastRun.GetValueOrDefault(vmid);
            if (run is not null)
            {
                code = run.Result;
                var when = run.Started.ToLocalTime().ToString("g", CultureInfo.InvariantCulture);
                message = run.Result switch
                {
                    0 => $"{run.Message} ({when}, {run.Node})",
                    1 => $"done with warnings ({when}): {run.Message}",
                    _ => $"failed ({when}, {run.Node}): {run.Message}"
                };
            }
            else
            {
                code = 1;
                message = $"no backup run in the last {days} days";
            }

            if (code == 2)
            {
                failed.Add($"{label}: {message}");
            }
            else if (code == 1)
            {
                warned.Add($"{label}: {message}");
            }

            channels.Add(new SensorChannelValue
            {
                Key = $"{kind}.{vmid}.backup",
                Label = $"{label} backup",
                Value = code,
                MeasurementKind = SensorMeasurementKind.Count,
                State = code switch { 2 => SensorState.Critical, 1 => SensorState.Warning, _ => SensorState.Healthy },
                Message = message,
                LogByDefault = false
            });

            DateTimeOffset? newest = newestBackups.TryGetValue(vmid, out var stored) ? stored : null;
            if (run is { Result: < 2 } && (newest is null || run.Started > newest))
            {
                newest = run.Started;
            }

            if (newest is { } newestAt)
            {
                var age = Math.Round(Math.Max(0, (now - newestAt).TotalHours), 1);
                if (covered.Contains(vmid))
                {
                    ages.Add(age);
                }

                channels.Add(new SensorChannelValue
                {
                    Key = $"{kind}.{vmid}.backupAgeHours",
                    Label = $"{label} last backup",
                    Value = age,
                    Unit = "h",
                    MeasurementKind = SensorMeasurementKind.Count
                });
            }
        }

        var withoutJob = guests.Where(guest => !guest.Template && !covered.Contains(guest.VmId)).OrderBy(guest => guest.VmId).ToList();
        var summary = new List<SensorChannelValue>
        {
            new() { Key = FailedChannel, Label = "Guests whose last backup failed", Value = failed.Count, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = WarningChannel, Label = "Guests with warnings or no recent backup", Value = warned.Count, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = WithoutJobChannel, Label = "Guests in no backup job", Value = withoutJob.Count, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false },
            new() { Key = "backupJobs", Label = "Enabled backup jobs", Value = enabledJobs, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false },
            new() { Key = "failedRuns", Label = "Backup runs that failed as a whole", Value = runFailures.Count, MeasurementKind = SensorMeasurementKind.Count }
        };
        if (ages.Count > 0)
        {
            summary.Add(new SensorChannelValue { Key = OldestBackupChannel, Label = "Oldest last backup of a guest in a job", Value = ages.Max(), Unit = "h", MeasurementKind = SensorMeasurementKind.Count });
        }

        channels.InsertRange(0, summary);
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == FailedChannel }).ToList();

        var parts = new List<string>();
        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count} failed - {string.Join("; ", failed.Take(3))}{(failed.Count > 3 ? "; ..." : string.Empty)}");
        }

        if (runFailures.Count > 0)
        {
            parts.Add(string.Join("; ", runFailures.Take(2)));
        }

        if (warned.Count > 0)
        {
            parts.Add($"{warned.Count} with warnings - {string.Join("; ", warned.Take(3))}{(warned.Count > 3 ? "; ..." : string.Empty)}");
        }

        if (parts.Count == 0)
        {
            parts.Add(watched.Count == 0
                ? $"{enabledJobs} enabled backup job{(enabledJobs == 1 ? string.Empty : "s")}, no guest covered"
                : $"{watched.Count} guest{(watched.Count == 1 ? string.Empty : "s")} backed up by {enabledJobs} job{(enabledJobs == 1 ? string.Empty : "s")}"
                  + (ages.Count > 0 ? $", oldest last backup {Hours(ages.Max())}" : string.Empty));
        }

        if (withoutJob.Count > 0)
        {
            parts.Add($"in no job: {string.Join(", ", withoutJob.Take(5).Select(guest => guest.Name ?? guest.VmId.ToString(CultureInfo.InvariantCulture)))}{(withoutJob.Count > 5 ? ", ..." : string.Empty)}");
        }

        if (running > 0)
        {
            parts.Add($"{running} backup run{(running == 1 ? string.Empty : "s")} in progress");
        }

        if (notes.Count > 0)
        {
            parts.Add(string.Join("; ", notes.Distinct().Take(3)));
        }

        var text = string.Join(" - ", parts);
        return failed.Count > 0 || runFailures.Count > 0
            ? SensorExecutionResult.Critical(duration, text, failed.Count, FailedChannel, channels)
            : warned.Count > 0
                ? SensorExecutionResult.Warning(duration, text, 0, FailedChannel, channels)
                : SensorExecutionResult.Healthy(duration, text, 0, FailedChannel, channels);
    }

    private static string Hours(double hours) => hours < 48
        ? $"{hours.ToString("0", CultureInfo.InvariantCulture)} h ago"
        : $"{(hours / 24).ToString("0", CultureInfo.InvariantCulture)} days ago";

    private static bool IsOk(string status) => status.Equals("OK", StringComparison.OrdinalIgnoreCase);

    private static PveBackupJob ReadJob(JsonElement job) => new(
        Text(job, "id") ?? "job",
        Number(job, "enabled") != 0,
        Number(job, "all") == 1,
        Ids(Text(job, "vmid")),
        Ids(Text(job, "exclude")),
        Text(job, "pool"),
        Text(job, "node"),
        Text(job, "storage"),
        Text(job, "schedule"),
        Text(job, "comment"));

    private static PveGuest? ReadGuest(JsonElement guest) => Number(guest, "vmid") is { } vmid
        ? new PveGuest((int)vmid, Text(guest, "type") ?? "qemu", Text(guest, "name"), Text(guest, "node"), Text(guest, "pool"), Number(guest, "template") == 1)
        : null;

    private static PveTask? ReadTask(JsonElement task, string node) =>
        Text(task, "upid") is { Length: > 0 } upid && Number(task, "starttime") is { } start
            ? new PveTask(upid, Text(task, "node") ?? node, Text(task, "id"), DateTimeOffset.FromUnixTimeSeconds((long)start), Number(task, "endtime") is null ? null : Text(task, "status") ?? "unknown")
            : null;

    private static IReadOnlyList<int> Ids(string? list) => (list ?? string.Empty)
        .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1)
        .Where(id => id >= 0)
        .ToList();

    private static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList() : [];

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static double? Number(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    private static HttpMessageHandler DefaultHandler(bool verifySsl)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (!verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    [GeneratedRegex(@"Starting Backup of VM (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"Finished Backup of VM (\d+)(?:\s*\(([^)]*)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex FinishRegex();

    [GeneratedRegex(@"Backup of VM (\d+) failed\s*-?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FailRegex();

    /// <summary>The PVE API with the token header; refusals in words that fit backups (not disks).</summary>
    private sealed class Api(HttpClient client, Uri baseUri, string authorization)
    {
        public async Task<JsonElement> GetAsync(string path, string what, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Proxmox refused the API token (401) - check the token ID (user@realm!name) and its secret.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException($"Proxmox denied reading {what} (403) - give the API token the PVEAuditor role on / with Propagate (and to the TOKEN itself when it has privilege separation).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Proxmox answered {(int)response.StatusCode} {response.ReasonPhrase} for {what}.");
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
        }

        /// <summary>For the per-node and per-storage reads: null instead of an exception, so one node or storage cannot sink the check.</summary>
        public async Task<JsonElement?> TryGetAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                return await GetAsync(path, path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
