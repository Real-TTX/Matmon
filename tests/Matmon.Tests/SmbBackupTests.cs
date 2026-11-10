using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Backup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Backups to an SMB share: the path, a run with its retention, restoring from the share, and the job's password at rest.</summary>
public sealed class SmbBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-smb-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    public SmbBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            owned.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(@"\\nas\backup\matmon\daily", "nas", "backup", "matmon/daily")]
    [InlineData("//nas/backup", "nas", "backup", "")]
    [InlineData("smb://10.0.0.5/share/a", "10.0.0.5", "share", "a")]
    public void A_share_path_is_read_in_every_usual_spelling(string path, string server, string share, string folder)
    {
        var location = SmbLocation.Parse(path, "backup", "pw", null, out var problem)!;

        Assert.Null(problem);
        Assert.Equal((server, share, folder), (location.Server, location.Share, location.Folder));
    }

    [Theory]
    [InlineData("nas/backup")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\nas\share\..\other")]
    [InlineData(@"\\nas\share\a;rm")]
    [InlineData(@"\\nas\share\a""b")]
    public void A_path_that_is_no_share_or_tries_to_escape_is_refused_in_words(string path)
    {
        Assert.Null(SmbLocation.Parse(path, null, null, null, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void The_smbclient_listing_is_read_and_folders_are_left_out()
    {
        const string output = """
              .                                   D        0  Wed Oct 28 22:00:00 2026
              ..                                  D        0  Wed Oct 28 22:00:00 2026
              old                                 D        0  Wed Oct 28 21:00:00 2026
              matmon-backup-20261028-220000-abc.json      A    12345  Wed Oct 28 22:00:01 2026
              notes.txt                           N       17  Mon Oct  5 08:03:09 2026

                    1048576 blocks of size 1024. 524288 blocks available
            """;

        var files = SmbclientBackupClient.ParseListing(output);

        Assert.Equal(["matmon-backup-20261028-220000-abc.json", "notes.txt"], files.Select(file => file.Name));
        Assert.Equal(12345, files[0].Size);
        Assert.NotNull(files[1].ModifiedUtc);
    }

    [Theory]
    [InlineData("session setup failed: NT_STATUS_LOGON_FAILURE", "refused")]
    [InlineData("tree connect failed: NT_STATUS_BAD_NETWORK_NAME", "share does not exist")]
    [InlineData("NT_STATUS_ACCESS_DENIED making remote directory", "may not write")]
    public void The_share_answer_is_said_in_words(string output, string expected)
    {
        Assert.Contains(expected, SmbclientBackupClient.Explain(output));
    }

    private (InMemoryMonitoringWorkspaceStore Store, string Folder) NewStore(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        var telemetry = new SqliteTelemetryRepository(Path.Combine(folder, "t.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new SmbHostEnvironment(folder),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(folder, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        _owned.Add(telemetry);
        _owned.Add(store);
        return (store, folder);
    }

    [Fact]
    public async Task A_run_writes_one_file_keeps_only_the_retention_and_never_touches_other_files()
    {
        var (store, _) = NewStore("run");
        var share = new FakeShare();
        var location = SmbLocation.Parse(@"\\nas\backup\matmon", "u", "p", null, out _)!;
        await share.WriteAsync(location, "someone-elses.json", [1], default);
        var job = store.CreateBackupJob(new WorkspaceBackupJob
        {
            Name = "NAS",
            Destination = BackupDestination.Smb,
            SmbPath = @"\\nas\backup\matmon",
            SmbUsername = "u",
            SmbPassword = "p",
            RetentionCount = 2,
            Sections = WorkspaceBackupSection.Topology
        });
        var runner = new RemoteBackupRunner(store, share, NullLogger<RemoteBackupRunner>.Instance);

        for (var run = 0; run < 3; run++)
        {
            var (success, message) = await runner.RunAsync(job, default);
            Assert.True(success, message);
            await Task.Delay(1100); // the file name carries the second
        }

        var mine = share.Files.Keys.Where(name => RemoteBackupRunner.BelongsTo(name, job)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Contains("someone-elses.json", share.Files.Keys);
        Assert.Equal("ok", store.FindBackupJob(job.Id)!.LastStatus);
    }

    [Fact]
    public async Task A_backup_on_the_share_restores_like_a_local_one()
    {
        var (source, _) = NewStore("source");
        var root = source.GetAllElements().OfType<ProbeElement>().First().Id;
        source.CreateSensor(root, "Kept on the NAS", "ping", "10.0.0.3", null);
        var share = new FakeShare();
        var job = source.CreateBackupJob(new WorkspaceBackupJob { Name = "NAS", Destination = BackupDestination.Smb, SmbPath = @"\\nas\b", Sections = WorkspaceBackupSection.Topology });
        await new RemoteBackupRunner(source, share, NullLogger<RemoteBackupRunner>.Instance).RunAsync(job, default);

        // Another instance with the same job reads the share and restores.
        var (target, _) = NewStore("target");
        var targetJob = target.CreateBackupJob(source.FindBackupJob(job.Id)!.Clone());
        var runner = new RemoteBackupRunner(target, share, NullLogger<RemoteBackupRunner>.Instance);
        var file = Assert.Single(await runner.ListAsync(targetJob, default));
        var snapshot = await runner.CopyToLocalAsync(targetJob, file.Name, default);
        target.RestoreBackupSnapshot(snapshot.FileName, WorkspaceBackupSection.Topology);

        Assert.Contains(target.GetAllElements().OfType<SensorElement>(), sensor => sensor.Name == "Kept on the NAS");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CopyToLocalAsync(targetJob, "../../etc/passwd", default));
    }

    [Fact]
    public void The_share_password_is_never_written_in_the_clear_and_a_blank_field_keeps_it()
    {
        var (store, folder) = NewStore("secret");
        var job = store.CreateBackupJob(new WorkspaceBackupJob { Name = "NAS", Destination = BackupDestination.Smb, SmbPath = @"\\nas\b", SmbUsername = "u", SmbPassword = "nas-password-42" });
        var edited = store.FindBackupJob(job.Id)!;
        edited.SmbPassword = null; // the editor posts a blank field
        store.UpdateBackupJob(edited);
        store.Dispose();

        Assert.DoesNotContain("nas-password-42", File.ReadAllText(Path.Combine(folder, "ws.json")));
        Assert.Equal("nas-password-42", store.FindBackupJob(job.Id)!.SmbPassword);
    }

    [Theory]
    [InlineData("https://cloud.example/remote.php/dav/files/me/matmon", true)]
    [InlineData("davs://cloud.example/dav/matmon", true)]
    [InlineData("http://192.168.1.10:5005/backup", true)]   // plain http inside the house
    [InlineData("http://cloud.example/dav/matmon", false)] // ...but not across the internet: the password travels along
    [InlineData("https://me:secret@cloud.example/dav", false)]
    [InlineData("ftp://cloud.example/dav", false)]
    [InlineData("https://cloud.example/dav?x=1", false)]
    public void A_webdav_address_is_read_strictly(string address, bool accepted) =>
        Assert.Equal(accepted, WebDavLocation.Parse(address, "me", "pw", out _) is not null);

    [Fact]
    public async Task A_webdav_job_creates_its_folder_keeps_the_retention_and_restores()
    {
        var (store, _) = NewStore("webdav");
        var root = store.GetAllElements().OfType<ProbeElement>().First().Id;
        store.CreateSensor(root, "Kept on WebDAV", "ping", "10.0.0.4", null);
        var dav = new FakeWebDav("me", "app-password");
        var job = store.CreateBackupJob(new WorkspaceBackupJob
        {
            Name = "Nextcloud",
            Destination = BackupDestination.WebDav,
            SmbPath = "https://cloud.example/remote.php/dav/files/me/backups/matmon",
            SmbUsername = "me",
            SmbPassword = "app-password",
            RetentionCount = 2,
            Sections = WorkspaceBackupSection.Topology
        });
        var runner = new RemoteBackupRunner(store, new FakeShare(), NullLogger<RemoteBackupRunner>.Instance, new DavClients(dav));

        for (var run = 0; run < 3; run++)
        {
            var (success, message) = await runner.RunAsync(job, default);
            Assert.True(success, message);
            await Task.Delay(1100);
        }

        Assert.Contains("/remote.php/dav/files/me/backups/", dav.Folders); // created on the way
        var files = await runner.ListAsync(job, default);
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.True(file.Size > 0));

        var snapshot = await runner.CopyToLocalAsync(job, files[0].Name, default);
        Assert.NotNull(snapshot);

        // A wrong password is said in words.
        var wrong = runner.Folder(BackupDestination.WebDav, job.SmbPath, "me", "nope", null, out _)!;
        var failure = await RemoteBackupRunner.TestAsync(wrong, default);
        Assert.Contains("401", failure);
    }

    private sealed class DavClients(FakeWebDav dav) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(dav, disposeHandler: false);
    }

    /// <summary>A minimal WebDAV server: Basic auth, MKCOL, PUT (409 when the folder is missing, as Nextcloud answers),
    /// PROPFIND depth 1, GET, DELETE.</summary>
    private sealed class FakeWebDav(string user, string password) : HttpMessageHandler
    {
        public HashSet<string> Folders { get; } = ["/", "/remote.php/", "/remote.php/dav/", "/remote.php/dav/files/", "/remote.php/dav/files/me/"];

        public Dictionary<string, byte[]> Files { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            if (request.Headers.Authorization?.ToString() != expected)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            }

            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var parent = path.EndsWith('/') ? path[..(path.TrimEnd('/').LastIndexOf('/') + 1)] : path[..(path.LastIndexOf('/') + 1)];
            switch (request.Method.Method)
            {
                case "MKCOL":
                    if (Folders.Contains(path)) { return new HttpResponseMessage(System.Net.HttpStatusCode.MethodNotAllowed); }
                    if (!Folders.Contains(parent)) { return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict); }
                    Folders.Add(path);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
                case "PUT":
                    if (!Folders.Contains(parent)) { return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict); }
                    Files[path] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
                case "GET":
                    return Files.TryGetValue(path, out var bytes)
                        ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                        : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                case "DELETE":
                    return new HttpResponseMessage(Files.Remove(path) ? System.Net.HttpStatusCode.NoContent : System.Net.HttpStatusCode.NotFound);
                case "PROPFIND":
                    if (!Folders.Contains(path)) { return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound); }
                    var xml = new StringBuilder("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">");
                    xml.Append($"<d:response><d:href>{path}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>");
                    foreach (var (name, content) in Files.Where(file => file.Key.StartsWith(path, StringComparison.Ordinal) && !file.Key[path.Length..].Contains('/')))
                    {
                        xml.Append($"<d:response><d:href>{Uri.EscapeDataString(name).Replace("%2F", "/")}</d:href><d:propstat><d:prop><d:getcontentlength>{content.Length}</d:getcontentlength><d:getlastmodified>Tue, 10 Nov 2026 18:00:00 GMT</d:getlastmodified><d:resourcetype/></d:prop></d:propstat></d:response>");
                    }

                    xml.Append("</d:multistatus>");
                    return new HttpResponseMessage((System.Net.HttpStatusCode)207) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
                default:
                    return new HttpResponseMessage(System.Net.HttpStatusCode.MethodNotAllowed);
            }
        }
    }

    private sealed class FakeShare : ISmbBackupClient
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public Task WriteAsync(SmbLocation location, string fileName, byte[] content, CancellationToken cancellationToken)
        {
            Files[fileName] = content;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SmbFile>> ListAsync(SmbLocation location, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SmbFile>>(Files.Select(file => new SmbFile(file.Key, file.Value.Length, DateTimeOffset.UtcNow)).ToList());

        public Task<byte[]> ReadAsync(SmbLocation location, string fileName, CancellationToken cancellationToken) => Task.FromResult(Files[fileName]);

        public Task DeleteAsync(SmbLocation location, string fileName, CancellationToken cancellationToken)
        {
            Files.Remove(fileName);
            return Task.CompletedTask;
        }
    }
}

file sealed class SmbHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
