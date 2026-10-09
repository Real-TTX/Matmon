using System.Text;
using System.Text.Json.Nodes;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The MIBs an admin uploaded are files in data/mibs, not part of workspace.json, so a backup has to pull them in and
/// a restore has to write them back explicitly - otherwise a rebuilt instance walks a switch and shows bare OIDs again
/// until someone remembers which vendor files they once uploaded.
/// <para>
/// Two things matter as much as the happy path: the section must stay OUT of the cloud mask (a vendor MIB library can
/// run to tens of megabytes), and a restore must treat the package as untrusted - it names the file after the module
/// inside it, never after anything the package says.
/// </para>
/// </summary>
public sealed class MibBackupTests : IDisposable
{
    private const string AcmeOid = "1.3.6.1.4.1.99999.1.0";

    private const string AcmeMib = """
        ACME-BACKUP-MIB DEFINITIONS ::= BEGIN
        acmeBackupRoot OBJECT IDENTIFIER ::= { iso 3 6 1 4 1 99999 }
        acmeBackupTemperature OBJECT-TYPE
            SYNTAX INTEGER
            MAX-ACCESS read-only
            STATUS current
            DESCRIPTION "Case temperature."
            ::= { acmeBackupRoot 1 }
        END
        """;

    private const string OtherMib = """
        ACME-OTHER-MIB DEFINITIONS ::= BEGIN
        acmeOtherRoot OBJECT IDENTIFIER ::= { iso 3 6 1 4 1 99998 }
        acmeOtherFan OBJECT-TYPE
            SYNTAX INTEGER
            MAX-ACCESS read-only
            STATUS current
            DESCRIPTION "Fan speed."
            ::= { acmeOtherRoot 1 }
        END
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-mibbackup-" + Guid.NewGuid().ToString("N"));

    public MibBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup must not fail the run (a SQLite handle can linger on Windows).
        }
    }

    private (InMemoryMonitoringWorkspaceStore Store, MibLibrary Library, IDisposable Telemetry) NewStore(string name, string? builtInDirectory = null)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var library = new MibLibrary(root, builtInDirectory ?? Path.Combine(root, "no-built-in"), NullLogger<MibLibrary>.Instance);
        var telemetry = new SqliteTelemetryRepository(Path.Combine(root, "t.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new MibBackupHostEnvironment(root),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(root, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance,
            notificationSink: null,
            mibLibrary: library);
        return (store, library, telemetry);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void A_local_backup_carries_the_uploaded_mibs_and_a_restore_brings_them_back()
    {
        var (source, sourceLibrary, sourceTelemetry) = NewStore("source");
        byte[] package;
        using (source)
        using (sourceTelemetry)
        {
            Assert.Empty(sourceLibrary.Upload([("acme.mib", Bytes(AcmeMib)), ("other.mib", Bytes(OtherMib))]).Rejected);
            package = source.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        }

        var (target, targetLibrary, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            Assert.Null(targetLibrary.Translate(AcmeOid));

            target.RestoreBackupBytes(package, WorkspaceBackupSection.All);

            var translation = targetLibrary.Translate(AcmeOid);
            Assert.NotNull(translation);
            Assert.Equal("acmeBackupTemperature.0", translation!.Name);
            Assert.Equal("ACME-BACKUP-MIB", translation.Node.Module);
            Assert.Equal("acmeOtherFan.0", targetLibrary.Translate("1.3.6.1.4.1.99998.1.0")!.Name);

            // Named after the module inside, exactly as an upload would have named it.
            Assert.True(File.Exists(Path.Combine(targetLibrary.UploadDirectory, "ACME-BACKUP-MIB.mib")));
            Assert.True(File.Exists(Path.Combine(targetLibrary.UploadDirectory, "ACME-OTHER-MIB.mib")));
        }
    }

    [Fact]
    public void A_restore_adds_to_what_is_there_and_never_deletes_a_newer_upload()
    {
        var (source, sourceLibrary, sourceTelemetry) = NewStore("source");
        byte[] package;
        using (source)
        using (sourceTelemetry)
        {
            sourceLibrary.Upload([("acme.mib", Bytes(AcmeMib))]);
            package = source.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        }

        var (target, targetLibrary, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            targetLibrary.Upload([("other.mib", Bytes(OtherMib))]);

            target.RestoreBackupBytes(package, WorkspaceBackupSection.All);

            Assert.NotNull(targetLibrary.Translate(AcmeOid));
            Assert.NotNull(targetLibrary.Translate("1.3.6.1.4.1.99998.1.0"));
        }
    }

    [Fact]
    public void The_standard_set_ships_with_the_build_and_is_not_carried()
    {
        var builtIn = Path.Combine(_dir, "shipped");
        Directory.CreateDirectory(builtIn);
        File.WriteAllText(Path.Combine(builtIn, "SHIPPED-MIB.mib"), """
            SHIPPED-MIB DEFINITIONS ::= BEGIN
            shippedRoot OBJECT IDENTIFIER ::= { iso 3 6 1 4 1 99997 }
            END
            """);

        var (source, sourceLibrary, sourceTelemetry) = NewStore("source", builtIn);
        byte[] package;
        using (source)
        using (sourceTelemetry)
        {
            Assert.Contains(sourceLibrary.Registry.Modules, module => module.Name == "SHIPPED-MIB");
            sourceLibrary.Upload([("acme.mib", Bytes(AcmeMib))]);
            package = source.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        }

        // One entry: the upload. The shipped file is part of the image, so a restore onto any build already has it.
        var mibs = JsonNode.Parse(package)!["document"]!["mibs"]!.AsArray();
        Assert.Single(mibs);

        var (target, targetLibrary, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            target.RestoreBackupBytes(package, WorkspaceBackupSection.All);

            Assert.DoesNotContain(targetLibrary.Registry.Modules, module => module.Name == "SHIPPED-MIB");
            Assert.Single(Directory.GetFiles(targetLibrary.UploadDirectory));
        }
    }

    [Fact]
    public void The_cloud_config_mask_leaves_mibs_out()
    {
        // A vendor MIB library can run to tens of megabytes; a nightly cloud job would re-upload it every run.
        Assert.False(WorkspaceBackupSections.CloudConfig.HasFlag(WorkspaceBackupSection.Mibs));
        Assert.True(WorkspaceBackupSection.All.HasFlag(WorkspaceBackupSection.Mibs));
    }

    [Fact]
    public void A_backup_without_the_section_does_not_carry_the_files()
    {
        var (source, sourceLibrary, sourceTelemetry) = NewStore("source");
        byte[] package;
        using (source)
        using (sourceTelemetry)
        {
            sourceLibrary.Upload([("acme.mib", Bytes(AcmeMib))]);
            package = source.CreateBackupBytes(WorkspaceBackupSections.CloudConfig, "cloud");
        }

        Assert.Empty(JsonNode.Parse(package)!["document"]!["mibs"]!.AsArray());

        var (target, targetLibrary, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            target.RestoreBackupBytes(package, WorkspaceBackupSection.All);

            Assert.Null(targetLibrary.Translate(AcmeOid));
        }
    }

    [Fact]
    public void A_tampered_package_cannot_choose_what_is_written_or_where()
    {
        var (source, _, sourceTelemetry) = NewStore("source");
        byte[] package;
        using (source)
        using (sourceTelemetry)
        {
            package = source.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        }

        var root = JsonNode.Parse(package)!.AsObject();
        root["document"]!.AsObject()["mibs"] = new JsonArray(
            new JsonObject { ["data"] = Convert.ToBase64String(Bytes("<svg onload=alert(1)/>")) },          // not a MIB at all
            new JsonObject { ["data"] = "!!! not base64 !!!" },                                              // not even bytes
            new JsonObject { ["data"] = null },                                                              // nothing
            new JsonObject { ["data"] = Convert.ToBase64String(new byte[(int)MibLibrary.MaxFileBytes + 1]) }, // larger than a MIB can be
            new JsonObject { ["data"] = Convert.ToBase64String(Bytes(AcmeMib)) });                           // the one honest entry
        var tampered = Encoding.UTF8.GetBytes(root.ToJsonString());

        var (target, targetLibrary, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            target.RestoreBackupBytes(tampered, WorkspaceBackupSection.All);

            // Only the honest entry got through, under the name its own module gives it.
            var written = Directory.GetFiles(targetLibrary.UploadDirectory).Select(file => Path.GetFileName(file)!).ToArray();
            Assert.Equal(["ACME-BACKUP-MIB.mib"], written);
            Assert.NotNull(targetLibrary.Translate(AcmeOid));
        }
    }

    [Fact]
    public void A_package_with_a_null_list_restores_nothing_and_does_not_fail()
    {
        var (source, _, sourceTelemetry) = NewStore("source");
        byte[] package;
        using (source)
        using (sourceTelemetry)
        {
            package = source.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        }

        var root = JsonNode.Parse(package)!.AsObject();
        root["document"]!.AsObject()["mibs"] = null;

        var (target, targetLibrary, targetTelemetry) = NewStore("target");
        using (target)
        using (targetTelemetry)
        {
            target.RestoreBackupBytes(Encoding.UTF8.GetBytes(root.ToJsonString()), WorkspaceBackupSection.All);

            Assert.False(Directory.Exists(targetLibrary.UploadDirectory) && Directory.GetFiles(targetLibrary.UploadDirectory).Length > 0);
        }
    }

    [Fact]
    public void A_backup_and_restore_cycle_does_not_make_the_files_grow()
    {
        // A MIB is stored with a byte order mark of its own; reading it back must not carry the old one into the
        // text, or every cycle would stack another three bytes on the front.
        var (store, library, telemetry) = NewStore("cycle");
        using (store)
        using (telemetry)
        {
            library.Upload([("acme.mib", Encoding.UTF8.GetPreamble().Concat(Bytes(AcmeMib)).ToArray())]);
            var path = Path.Combine(library.UploadDirectory, "ACME-BACKUP-MIB.mib");
            var before = File.ReadAllBytes(path);

            for (var cycle = 0; cycle < 2; cycle++)
            {
                store.RestoreBackupBytes(store.CreateBackupBytes(WorkspaceBackupSection.All, "cycle"), WorkspaceBackupSection.Mibs);
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.True(before.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.False(before.AsSpan(3).StartsWith(Encoding.UTF8.GetPreamble()));
        }
    }

    [Fact]
    public void The_snapshot_lists_the_section_and_restoring_only_that_section_leaves_the_rest_alone()
    {
        var (store, library, telemetry) = NewStore("snapshot");
        using (store)
        using (telemetry)
        {
            library.Upload([("acme.mib", Bytes(AcmeMib))]);
            var job = store.CreateBackupJob(new WorkspaceBackupJob { Name = "local", Sections = WorkspaceBackupSection.All });
            var snapshot = store.RunBackupJob(job.Id, "test");

            var preview = store.FindBackupSnapshotDetails(snapshot.FileName)!.Sections.Single(section => section.Section == WorkspaceBackupSection.Mibs);
            Assert.True(preview.Included);
            Assert.Equal(1, preview.ItemCount);
            Assert.Equal("1 MIB file(s)", preview.Summary);

            library.Delete("ACME-BACKUP-MIB");
            store.CreateMap(new MonitoringMap { Name = "made after the snapshot" });
            Assert.Null(library.Translate(AcmeOid));

            store.RestoreBackupSnapshot(snapshot.FileName, WorkspaceBackupSection.Mibs);

            Assert.NotNull(library.Translate(AcmeOid));
            // Maps were not part of the restore, so what was made after the snapshot is still there.
            Assert.Contains(store.GetMaps(), map => map.Name == "made after the snapshot");
        }
    }
}

file sealed class MibBackupHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
