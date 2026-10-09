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
    public const int CurrentSchemaVersion = 5;

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
            store => store.MigrateBackupJobsToAllSections())
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
    private void RunSchemaMigrations()
    {
        var from = _document.SchemaVersion;
        var pending = Migrations.Where(migration => migration.Version > from).OrderBy(migration => migration.Version).ToArray();
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
}
