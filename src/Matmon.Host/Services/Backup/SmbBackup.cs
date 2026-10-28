using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;

namespace Matmon.Host.Services.Backup;

/// <summary>An SMB share and a folder in it, with the account that may write there.</summary>
public sealed record SmbLocation(string Server, string Share, string Folder, string? Username, string? Password, string? Domain)
{
    /// <summary>How it reads on a page: <c>\\nas\backup\matmon</c>.</summary>
    public string Display => $@"\\{Server}\{Share}{(Folder.Length == 0 ? "" : "\\" + Folder.Replace('/', '\\'))}";

    /// <summary>
    /// Reads <c>\\nas\share\folder</c>, <c>//nas/share/folder</c> or <c>smb://nas/share/folder</c>. Null with the reason
    /// when it is none of them - and a path with quotes, semicolons or ".." is refused: it ends up in a command line and in
    /// a share path, and neither may be steered somewhere else.
    /// </summary>
    public static SmbLocation? Parse(string? path, string? username, string? password, string? domain, out string? problem)
    {
        problem = null;
        var text = (path ?? string.Empty).Trim();
        if (text.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            text = "//" + text[6..];
        }

        text = text.Replace('\\', '/');
        if (!text.StartsWith("//", StringComparison.Ordinal))
        {
            problem = @"Enter the share as \\server\share\folder (or //server/share/folder).";
            return null;
        }

        var parts = text[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            problem = @"Name the server and the share: \\server\share (a folder inside it is optional).";
            return null;
        }

        if (parts.Any(part => part == ".." || part == "." || part.IndexOfAny(['"', ';', '\'', '`', '$', '|', '&', '<', '>', '*', '?']) >= 0))
        {
            problem = "The share path may not contain quotes, semicolons, wildcards or \"..\".";
            return null;
        }

        return new SmbLocation(
            parts[0],
            parts[1],
            string.Join('/', parts.Skip(2)),
            string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrWhiteSpace(domain) ? null : domain.Trim());
    }

    public static SmbLocation? For(WorkspaceBackupJob job, out string? problem) =>
        Parse(job.SmbPath, job.SmbUsername, job.SmbPassword, job.SmbDomain, out problem);
}

/// <summary>A file on the share.</summary>
public sealed record SmbFile(string Name, long Size, DateTimeOffset? ModifiedUtc);

/// <summary>Reading and writing files on an SMB share. Every call names the folder; nothing stays connected.</summary>
public interface ISmbBackupClient
{
    Task WriteAsync(SmbLocation location, string fileName, byte[] content, CancellationToken cancellationToken);

    Task<IReadOnlyList<SmbFile>> ListAsync(SmbLocation location, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(SmbLocation location, string fileName, CancellationToken cancellationToken);

    Task DeleteAsync(SmbLocation location, string fileName, CancellationToken cancellationToken);
}

/// <summary>
/// Windows: the share is connected with the job's account for the duration of a call (<c>WNetAddConnection2</c> - a
/// service running as LocalSystem has no network identity of its own a NAS would accept), then plain file IO on the UNC
/// path. A connection the call made is dropped again; one that existed before (the same share mapped by the admin) is left.
/// </summary>
public sealed class WindowsSmbBackupClient : ISmbBackupClient
{
    public async Task WriteAsync(SmbLocation location, string fileName, byte[] content, CancellationToken cancellationToken) =>
        await WithShareAsync(location, async folder =>
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, fileName);
            var temporary = target + ".part";
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, target, overwrite: true);
            return 0;
        });

    public Task<IReadOnlyList<SmbFile>> ListAsync(SmbLocation location, CancellationToken cancellationToken) =>
        WithShareAsync(location, folder => Task.FromResult<IReadOnlyList<SmbFile>>(
            Directory.Exists(folder)
                ? new DirectoryInfo(folder).GetFiles().Select(file => new SmbFile(file.Name, file.Length, file.LastWriteTimeUtc)).ToList()
                : []));

    public Task<byte[]> ReadAsync(SmbLocation location, string fileName, CancellationToken cancellationToken) =>
        WithShareAsync(location, folder => File.ReadAllBytesAsync(Path.Combine(folder, fileName), cancellationToken));

    public Task DeleteAsync(SmbLocation location, string fileName, CancellationToken cancellationToken) =>
        WithShareAsync(location, folder =>
        {
            File.Delete(Path.Combine(folder, fileName));
            return Task.FromResult(0);
        });

    private static async Task<T> WithShareAsync<T>(SmbLocation location, Func<string, Task<T>> body)
    {
        var share = $@"\\{location.Server}\{location.Share}";
        var connected = Connect(share, location);
        try
        {
            var folder = location.Folder.Length == 0 ? share : Path.Combine(share, location.Folder.Replace('/', '\\'));
            return await body(folder);
        }
        finally
        {
            if (connected)
            {
                WNetCancelConnection2(share, 0, true);
            }
        }
    }

    private static bool Connect(string share, SmbLocation location)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var resource = new NetResource { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = share };
        var user = location.Username is null ? null : location.Domain is null ? location.Username : $@"{location.Domain}\{location.Username}";
        var result = WNetAddConnection2(ref resource, location.Password, user, 0);
        return result switch
        {
            0 => true,
            // Already connected (the admin mapped it, or another call is running): use that connection, leave it alone.
            1219 or 85 => false,
            _ => throw new IOException($@"Cannot connect to {share}: {new Win32Exception(result).Message}")
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}

/// <summary>
/// Linux / Docker: Samba's <c>smbclient</c> (installed in the image) - no kernel mount, so no privileged container. The
/// password goes through the PASSWD environment variable, never the command line (which other processes can read).
/// </summary>
public sealed partial class SmbclientBackupClient : ISmbBackupClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task WriteAsync(SmbLocation location, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var local = Path.Combine(Path.GetTempPath(), $"matmon-smb-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(local, content, cancellationToken);
        try
        {
            // Every folder level is created first - an existing one only says so, which is fine.
            var path = string.Empty;
            foreach (var part in location.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? part : $"{path}/{part}";
                await RunAsync(location, $"mkdir \"{path}\"", cancellationToken, allowFailure: true);
            }

            await RunAsync(location, $"{Cd(location)}put \"{local}\" \"{fileName}\"", cancellationToken);
        }
        finally
        {
            File.Delete(local);
        }
    }

    public async Task<IReadOnlyList<SmbFile>> ListAsync(SmbLocation location, CancellationToken cancellationToken)
    {
        var output = await RunAsync(location, $"{Cd(location)}ls", cancellationToken);
        return ParseListing(output);
    }

    /// <summary>The files in an <c>ls</c> answer of smbclient (folders left out).</summary>
    public static IReadOnlyList<SmbFile> ParseListing(string output)
    {
        var files = new List<SmbFile>();
        foreach (Match match in ListLine().Matches(output))
        {
            if (match.Groups["attributes"].Value.Contains('D'))
            {
                continue;
            }

            DateTimeOffset? modified = DateTime.TryParseExact(
                Regex.Replace(match.Groups["date"].Value, @"\s+", " "),
                "ddd MMM d HH:mm:ss yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed)
                    ? new DateTimeOffset(parsed).ToUniversalTime()
                    : null;
            files.Add(new SmbFile(match.Groups["name"].Value.Trim(), long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture), modified));
        }

        return files;
    }

    public async Task<byte[]> ReadAsync(SmbLocation location, string fileName, CancellationToken cancellationToken)
    {
        var local = Path.Combine(Path.GetTempPath(), $"matmon-smb-{Guid.NewGuid():N}");
        try
        {
            await RunAsync(location, $"{Cd(location)}get \"{fileName}\" \"{local}\"", cancellationToken);
            return await File.ReadAllBytesAsync(local, cancellationToken);
        }
        finally
        {
            File.Delete(local);
        }
    }

    public Task DeleteAsync(SmbLocation location, string fileName, CancellationToken cancellationToken) =>
        RunAsync(location, $"{Cd(location)}del \"{fileName}\"", cancellationToken);

    private static string Cd(SmbLocation location) => location.Folder.Length == 0 ? string.Empty : $"cd \"{location.Folder}\"; ";

    private static async Task<string> RunAsync(SmbLocation location, string commands, CancellationToken cancellationToken, bool allowFailure = false)
    {
        var start = new ProcessStartInfo("smbclient")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add($"//{location.Server}/{location.Share}");
        if (location.Username is null)
        {
            start.ArgumentList.Add("-N");
        }
        else
        {
            start.ArgumentList.Add("-U");
            start.ArgumentList.Add(location.Username);
            start.Environment["PASSWD"] = location.Password ?? string.Empty;
        }

        if (location.Domain is not null)
        {
            start.ArgumentList.Add("-W");
            start.ArgumentList.Add(location.Domain);
        }

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(commands);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("smbclient did not start.");
        }
        catch (Win32Exception)
        {
            throw new IOException("smbclient is not installed on this machine (the Matmon image has it; on a plain Linux install: apt install smbclient).");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* gone already */ }
                throw new IOException($"The share {location.Display} did not answer in time.");
            }

            var text = await output + await error;
            var failed = process.ExitCode != 0 || text.Contains("NT_STATUS_", StringComparison.Ordinal);
            if (failed && !allowFailure)
            {
                throw new IOException($"{location.Display}: {Explain(text)}");
            }

            return text;
        }
    }

    /// <summary>The share's answer in words (smbclient speaks NT_STATUS codes).</summary>
    public static string Explain(string output)
    {
        var status = StatusCode().Match(output).Value;
        return status switch
        {
            "NT_STATUS_LOGON_FAILURE" => "the user name or password was refused",
            "NT_STATUS_ACCESS_DENIED" => "this account may not write there",
            "NT_STATUS_BAD_NETWORK_NAME" => "the share does not exist on that server",
            "NT_STATUS_OBJECT_PATH_NOT_FOUND" or "NT_STATUS_OBJECT_NAME_NOT_FOUND" => "the folder does not exist",
            "NT_STATUS_HOST_UNREACHABLE" or "NT_STATUS_IO_TIMEOUT" or "NT_STATUS_CONNECTION_REFUSED" => "the server does not answer on SMB (port 445)",
            "NT_STATUS_DISK_FULL" => "the share is full",
            "" => output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "smbclient failed",
            _ => status
        };
    }

    // "  name.json      A    1234  Wed Oct 28 22:00:00 2026"
    [GeneratedRegex(@"^\s{2}(?<name>.+?)\s+(?<attributes>[A-Z]*)\s+(?<size>\d+)\s+(?<date>\w{3}\s+\w{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4})\s*$", RegexOptions.Multiline)]
    private static partial Regex ListLine();

    [GeneratedRegex(@"NT_STATUS_[A-Z_]+")]
    private static partial Regex StatusCode();
}

/// <summary>
/// Backups on an SMB share: the scheduler's run, the page's list and "copy to this machine", and a test. The snapshot is the
/// same package a local job writes (sealed with the backup passphrase when one is set); a job's files carry its id, so its
/// retention never touches anything else in the folder.
/// </summary>
public sealed class SmbBackupRunner(IMonitoringWorkspaceStore store, ISmbBackupClient client, ILogger<SmbBackupRunner> logger)
{
    public const string FilePrefix = "matmon-backup-";

    public static string FileNameFor(WorkspaceBackupJob job, DateTimeOffset utc) =>
        $"{FilePrefix}{utc.ToUniversalTime():yyyyMMdd-HHmmss}-{job.Id:N}.json";

    public static bool BelongsTo(string fileName, WorkspaceBackupJob job) =>
        fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith($"-{job.Id:N}.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the snapshot, applies the retention and records the run. Never throws - the outcome is the job's status.</summary>
    public async Task<(bool Success, string Message)> RunAsync(WorkspaceBackupJob job, CancellationToken cancellationToken)
    {
        if (SmbLocation.For(job, out var problem) is not { } location)
        {
            store.RecordRemoteBackupJobRun(job.Id, false, problem!, null);
            return (false, problem!);
        }

        try
        {
            var bytes = store.CreateBackupBytes(job.Sections, "Scheduled backup to an SMB share.");
            var name = FileNameFor(job, DateTimeOffset.UtcNow);
            await client.WriteAsync(location, name, bytes, cancellationToken);

            var removed = 0;
            var mine = (await client.ListAsync(location, cancellationToken))
                .Where(file => BelongsTo(file.Name, job))
                .OrderByDescending(file => file.Name, StringComparer.Ordinal) // the stamp sorts by time
                .ToList();
            foreach (var old in mine.Skip(Math.Max(1, job.RetentionCount)))
            {
                await client.DeleteAsync(location, old.Name, cancellationToken);
                removed++;
            }

            var message = $"Saved {name} to {location.Display}{(removed > 0 ? $", {removed} older removed" : "")}.";
            store.RecordRemoteBackupJobRun(job.Id, true, message, bytes.LongLength);
            logger.LogInformation("SMB backup job {Job}: {Message}", job.Name, message);
            return (true, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = $"Not saved: {exception.Message}";
            store.RecordRemoteBackupJobRun(job.Id, false, message, null);
            logger.LogWarning(exception, "SMB backup job {Job} failed", job.Name);
            return (false, message);
        }
    }

    /// <summary>This job's backups on the share, newest first.</summary>
    public async Task<IReadOnlyList<SmbFile>> ListAsync(WorkspaceBackupJob job, CancellationToken cancellationToken)
    {
        var location = SmbLocation.For(job, out var problem) ?? throw new IOException(problem);
        return (await client.ListAsync(location, cancellationToken))
            .Where(file => BelongsTo(file.Name, job))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Copies one of the job's backups to this machine's backups, where the normal restore takes it.</summary>
    public async Task<WorkspaceBackupSnapshotInfo> CopyToLocalAsync(WorkspaceBackupJob job, string fileName, CancellationToken cancellationToken)
    {
        if (!BelongsTo(fileName, job) || fileName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidOperationException("That file is not one of this job's backups.");
        }

        var location = SmbLocation.For(job, out var problem) ?? throw new IOException(problem);
        var bytes = await client.ReadAsync(location, fileName, cancellationToken);
        using var stream = new MemoryStream(bytes);
        return store.ImportBackupSnapshot(stream, fileName);
    }

    /// <summary>Writes, lists, reads back and deletes a small file - says in words what does not work.</summary>
    public async Task<string?> TestAsync(SmbLocation location, CancellationToken cancellationToken)
    {
        var name = $"matmon-test-{Guid.NewGuid():N}.txt";
        var content = Encoding.UTF8.GetBytes($"Matmon backup test {DateTimeOffset.UtcNow:O}");
        try
        {
            await client.WriteAsync(location, name, content, cancellationToken);
            var listed = await client.ListAsync(location, cancellationToken);
            if (!listed.Any(file => file.Name == name))
            {
                return "the file was written but is not listed - check the folder";
            }

            var back = await client.ReadAsync(location, name, cancellationToken);
            await client.DeleteAsync(location, name, cancellationToken);
            return back.SequenceEqual(content) ? null : "the file read back differs from what was written";
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return exception.Message;
        }
    }
}
