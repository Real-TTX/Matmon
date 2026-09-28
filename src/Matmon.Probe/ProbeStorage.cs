namespace Matmon.Probe;

/// <summary>
/// The storage facts the Probe Health sensor judges: is the probe's data directory there, how much is in it,
/// and how much room is left on the drive it lives on.
///
/// Deliberately smaller than the Host's StorageOverview. A primary (or the Docker probe, which runs the same
/// Host) has a workspace file, a telemetry database and a backup folder; an agent installed on a customer's
/// machine has none of those - its "data" is its own directory. What both DO have is a directory that has to
/// exist and a drive that must not fill up, and that is all probe-health ever read.
/// </summary>
public sealed record ProbeStorageSnapshot(
    bool DataDirectoryExists,
    long DataDirectoryBytes,
    int DataFileCount,
    long? DriveAvailableBytes,
    double? DriveFreePercent,
    string? ErrorMessage)
{
    public double DataDirectoryMegabytes => Math.Round(DataDirectoryBytes / 1024.0 / 1024.0, 2);

    public double? DriveAvailableGigabytes => DriveAvailableBytes.HasValue
        ? Math.Round(DriveAvailableBytes.Value / 1024.0 / 1024.0 / 1024.0, 2)
        : null;
}

/// <summary>Where a probe keeps its data - implemented by the Host (workspace directory) and by the agent.</summary>
public interface IProbeStorageSource
{
    ProbeStorageSnapshot GetSnapshot();
}

/// <summary>A probe whose data is simply one directory - the agent's case.</summary>
public sealed class DirectoryProbeStorageSource(string directory) : IProbeStorageSource
{
    public ProbeStorageSnapshot GetSnapshot()
    {
        var scan = ProbeStorageScanner.ScanDirectory(directory);
        var drive = ProbeStorageScanner.ResolveDrive(directory);

        return new ProbeStorageSnapshot(
            Directory.Exists(directory),
            scan.TotalBytes,
            scan.FileCount,
            drive?.AvailableFreeSpace,
            ProbeStorageScanner.CalculateFreePercent(drive),
            scan.ErrorMessage);
    }
}

/// <summary>The directory walk and drive lookup, shared with the Host's StorageOverviewProvider.</summary>
public static class ProbeStorageScanner
{
    public static ProbeDirectoryScan ScanDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return new ProbeDirectoryScan(0, 0, "data directory does not exist");
        }

        long totalBytes = 0;
        var fileCount = 0;
        string? firstError = null;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        totalBytes += info.Exists ? info.Length : 0;
                        fileCount++;
                    }
                    catch (Exception ex)
                    {
                        firstError ??= ex.Message;
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        return new ProbeDirectoryScan(totalBytes, fileCount, firstError);
    }

    public static DriveInfo? ResolveDrive(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);

            // The MOUNT the path lives on, not the root of its path. On Linux every path's root is "/", so
            // the old lookup always measured the root filesystem - for an agent under /opt on a machine with
            // a separate /opt volume, or a Docker data volume, that is a different disk than the one filling up.
            // The longest mount point that prefixes the path is the one it is actually stored on.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mounted = DriveInfo.GetDrives()
                .Where(candidate => IsUnder(fullPath, candidate.RootDirectory.FullName, comparison))
                .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
                .FirstOrDefault(candidate => candidate.IsReady && candidate.TotalSize > 0);
            if (mounted is not null)
            {
                return mounted;
            }

            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string mountPoint, StringComparison comparison)
    {
        if (!path.StartsWith(mountPoint, comparison))
        {
            return false;
        }

        // "/data" must not claim "/database": the prefix has to end on a directory boundary.
        return path.Length == mountPoint.Length ||
            mountPoint.EndsWith(Path.DirectorySeparatorChar) ||
            path[mountPoint.Length] == Path.DirectorySeparatorChar;
    }

    public static double? CalculateFreePercent(DriveInfo? drive)
    {
        if (drive is null || drive.TotalSize <= 0)
        {
            return null;
        }

        return Math.Round((double)drive.AvailableFreeSpace / drive.TotalSize * 100.0, 2);
    }
}

public sealed record ProbeDirectoryScan(long TotalBytes, int FileCount, string? ErrorMessage);
