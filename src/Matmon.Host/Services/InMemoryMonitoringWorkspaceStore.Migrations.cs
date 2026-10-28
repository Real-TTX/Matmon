using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Probe.Plugins;
using Microsoft.Extensions.Logging;

namespace Matmon.Host.Services;

/// <summary>
/// THE central place for workspace schema migrations - the instance's equivalent of the cloud's EF Core
/// migration folder.
///
/// Before this, a one-time migration was a method call hand-placed in the store constructor with no version
/// anywhere: every step re-ran on every boot and had to work out for itself whether it had already happened,
/// and the ORDER carried logic that only a comment recorded. <see cref="WorkspaceDocument.SchemaVersion"/>
/// plus the ordered list below makes both facts data: a step declares its number, runs once, and the document
/// records how far it has come.
///
/// Adding one: append a record with the next version, bump <see cref="CurrentSchemaVersion"/>, and write the
/// migration as a private method on the store like the existing ones. Never renumber or reuse a version - a
/// workspace in the field has already recorded the old numbering.
///
/// NOT in here, on purpose:
/// <list type="bullet">
/// <item><b>The <c>Ensure*</c> seeders/normalisers.</b> They are INVARIANTS, not migrations - "this workspace
/// always has a sensor-definition catalog / an alert collection / at least one admin" is true on every boot,
/// not once. They keep running on every load, after this pipeline.</item>
/// <item><b>Map layouts.</b> Each <see cref="Matmon.Core.Domain.MonitoringMap"/> carries its own
/// <c>LayoutVersion</c> and is migrated by <c>MonitoringMapLayoutMigration</c>, because a map can also arrive
/// later - restored from a backup, or imported - long after the document itself is current. A per-document
/// version could not express that.</item>
/// <item><b>The telemetry database.</b> SQLite has its own schema and its own migration inside
/// <c>SqliteTelemetryRepository</c>; the one step here is the move of telemetry OUT of workspace.json, which
/// is a change to the document.</item>
/// </list>
/// </summary>
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>The schema version a workspace saved by THIS build carries. Bump when adding a migration.</summary>
    public const int CurrentSchemaVersion = 11;

    private sealed record SchemaMigration(int Version, string Name, Action<InMemoryMonitoringWorkspaceStore> Apply);

    /// <summary>The ordered, numbered list. A document at version N runs everything above N, in order.</summary>
    private static readonly SchemaMigration[] Migrations =
    [
        new(1, "Move telemetry out of workspace.json into telemetry.db",
            store => store.MigrateDocumentTelemetryIntoRepository()),

        // Must precede the catalog rebuild, which only prunes a retired definition once nothing references
        // it - so rewriting the sensors first drops the definition in the same startup rather than the next.
        // That ordering used to live in a comment beside two adjacent calls; here it is the version numbers.
        new(2, "Rewrite sensors using retired types (proxmox scope -> proxmox-health / -node-health)",
            store => store.MigrateRetiredProxmoxSensors()),

        new(3, "Bake live template links into the element (copy + origin model)", store =>
        {
            // Its prerequisite, stated rather than implied by call order: the bake resolves a link against
            // the template list, so the built-in templates have to exist first. EnsureDefaultTemplates is an
            // invariant and idempotent, so calling it early costs nothing.
            store.EnsureDefaultTemplates();
            store.MigrateAppliedTemplatesToCopies();
        }),

        new(4, "Move legacy ssl.warningDays/criticalDays parameters onto channel thresholds",
            store => store.MigrateSslCertificateThresholds()),

        new(5, "Local backup jobs that selected every section also get the sections added since (map images, MIBs)",
            store => store.MigrateBackupJobsToAllSections()),

        new(6, "Bind every agent to a host (its machine's sensors move under it)",
            store => store.MigrateAgentProbesToBoundHosts()),

        new(7, "UPS SNMP sensors read the on-battery values and alarm on a power cut",
            store => store.MigrateUpsSensorsToPowerChannels()),

        new(8, "Rules the load fix-up had pointed at a webhook sender while they mail a user group go back to the default SMTP",
            store => store.MigrateGuessedWebhookSendersOffUserGroupRules()),

        new(9, "The primary's syslog and SNMP trap receivers become collector plugins on the main probe",
            store => store.MigrateReceiversToPlugins()),

        new(10, "The built-in browser notification sender and its rule (to the registered devices of all users)",
            store => store.MigrateAddBrowserPushRule()),

        new(11, "Local backup jobs that held every section also get the collectors, the general settings and (with the history) the syslog messages",
            store => store.MigrateBackupJobsToCollectorSections())
    ];

    /// <summary>The registry as plain data, for tests that assert its shape (unique, ascending, and in step
    /// with <see cref="CurrentSchemaVersion"/>) - the things a reviewer cannot see by reading the list.</summary>
    public static IReadOnlyList<(int Version, string Name)> SchemaMigrationCatalog { get; } =
        Migrations.Select(migration => (migration.Version, migration.Name)).ToArray();

    /// <summary>Applies every migration the loaded document has not seen, in order, and records how far it
    /// got. Must hold <c>_gate</c> (the constructor runs before anything else can reach the store).
    ///
    /// A workspace written before this pipeline existed has no version, i.e. 0, so it runs the whole list -
    /// which is exactly what it got on every single boot until now, and every step is still idempotent. The
    /// difference is that from here on it runs ONCE.</summary>
    private void RunSchemaMigrations(IReadOnlyCollection<int>? skipVersions = null)
    {
        var from = _document.SchemaVersion;
        var pending = Migrations
            .Where(migration => migration.Version > from && skipVersions?.Contains(migration.Version) != true)
            .OrderBy(migration => migration.Version)
            .ToArray();
        if (pending.Length == 0 && from < CurrentSchemaVersion)
        {
            _document.SchemaVersion = CurrentSchemaVersion;
            return;
        }
        if (pending.Length == 0)
        {
            return;
        }

        foreach (var migration in pending)
        {
            _logger.LogInformation("Workspace migration {Version}: {Name}", migration.Version, migration.Name);
            migration.Apply(this);
        }

        _document.SchemaVersion = CurrentSchemaVersion;
        _logger.LogInformation(
            "Workspace schema migrated from {From} to {To} ({Count} step(s))",
            from,
            CurrentSchemaVersion,
            pending.Length);
        QueueSave(SavePriority.Configuration);
    }

    /// <summary>
    /// Migration 7. A UPS SNMP sensor used to read charge, runtime, load and battery status only, so a power cut stayed
    /// green until the battery had drained below 50 %. A sensor that still carries exactly the old OID list gets the new
    /// one (seconds on battery, output source, input voltage, alarms) - one whose list was edited is left alone - and
    /// every UPS SNMP sensor gets the NEW alarms where it has none: on battery and low battery critical, on bypass a
    /// warning. The charge/runtime/load thresholds are not touched: an admin may have removed one on purpose.
    /// </summary>
    private void MigrateUpsSensorsToPowerChannels()
    {
        var sensors = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .Where(sensor => string.Equals(sensor.SensorTypeKey, UpsSnmpSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var sensor in sensors)
        {
            if (sensor.Settings.Parameters.TryGetValue("snmp.oids", out var oids) &&
                UpsSnmpSensorExecutor.IsOidList(oids, UpsSnmpSensorExecutor.LegacyDefaultOids))
            {
                sensor.Settings.Parameters["snmp.oids"] = UpsSnmpSensorExecutor.DefaultOids;
            }

            foreach (var (channel, severity) in new[] { (UpsChannels.OnBattery, "critical"), (UpsChannels.LowBattery, "critical"), (UpsChannels.OnBypass, "warning") })
            {
                if (!MonitoringSettings.TryReadChannelThreshold(sensor.Settings, channel, severity, out _))
                {
                    MonitoringSettings.SetChannelThreshold(sensor.Settings, channel, severity, new ThresholdRule(ThresholdDirection.Above, 0));
                }
            }
        }
    }

    /// <summary>
    /// Migration 8. The load-time fix-up gave every rule without a sender "the first sender there is" - a webhook sender
    /// too - so the seeded default rule (no sender = the default SMTP, to the built-in "All users") became a webhook rule
    /// the moment an admin created one, and its e-mail alerts stopped. A webhook sender with a built-in user group as the
    /// receiver is that mistake and nothing else (a group expands to e-mail addresses, which no webhook takes), so such a
    /// rule goes back to no sender. The fix-up itself no longer guesses.
    /// </summary>
    private void MigrateGuessedWebhookSendersOffUserGroupRules()
    {
        foreach (var rule in _document.NotificationRules ?? [])
        {
            var sender = rule.SenderId is Guid senderId ? (_document.NotificationSenders ?? []).FirstOrDefault(candidate => candidate.Id == senderId) : null;
            if (sender is { Kind: NotificationEndpointKind.Webhook } && NotificationReceiverDefaults.IsBuiltIn(rule.ReceiverId))
            {
                rule.SenderId = null;
                rule.ChannelKind = NotificationChannelKind.Email;
            }
        }
    }

    /// <summary>
    /// Migration 9. The primary's own syslog and SNMP trap receivers were switched on under System > Syslog / SNMP traps;
    /// now receiving is a plugin's job wherever it runs, the main probe included, and the pages come with the plugin. A
    /// receiver that was ON becomes the collector plugin on the main probe with the same ports and allow-list (unless one is
    /// already assigned there); one that was off becomes nothing - the plugin is not switched on behind the admin's back.
    /// The old switch is cleared either way: the plugin assignment is where it lives now.
    /// </summary>
    private void MigrateReceiversToPlugins()
    {
        var root = _document.RootProbe;
        var plugins = _document.Plugins ??= [];
        bool Assigned(string pluginId) => plugins.Any(assignment =>
            assignment.ProbeElementId == root.Id && string.Equals(assignment.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        static string Flag(bool value) => value ? "true" : "false";
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        var syslog = _document.Syslog;
        if (syslog is { Enabled: true } && !Assigned(SyslogCollectorPlugin.Id))
        {
            var normalized = syslog.Normalized();
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["udpEnabled"] = Flag(normalized.UdpEnabled),
                ["udpPort"] = Number(normalized.UdpPort),
                ["tcpEnabled"] = Flag(normalized.TcpEnabled),
                ["tcpPort"] = Number(normalized.TcpPort)
            };
            if (normalized.AllowedSources is { } allowed)
            {
                settings["allowedSources"] = allowed;
            }

            plugins.Add(new PluginAssignment { PluginId = SyslogCollectorPlugin.Id, ProbeElementId = root.Id, Settings = settings });
        }

        if (syslog is not null)
        {
            syslog.Enabled = false;
        }

        var traps = _document.SnmpTraps;
        if (traps is { Enabled: true } && !Assigned(SnmpTrapCollectorPlugin.Id))
        {
            var normalized = traps.Normalized();
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["port"] = Number(normalized.Port) };
            if (normalized.AllowedSources is { } allowed)
            {
                settings["allowedSources"] = allowed;
            }

            plugins.Add(new PluginAssignment { PluginId = SnmpTrapCollectorPlugin.Id, ProbeElementId = root.Id, Settings = settings });
        }

        if (traps is not null)
        {
            traps.Enabled = false;
        }
    }
}
