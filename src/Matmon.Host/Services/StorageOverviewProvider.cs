using System.Globalization;

namespace Matmon.Host.Services;

/// <summary>
/// The Host's full storage picture (workspace, telemetry, backups, data directory). Also the Host's
/// <see cref="IProbeStorageSource"/>: probe-health on a primary or a Docker probe judges this data directory.
/// </summary>
public sealed class StorageOverviewProvider : IProbeStorageSource
{
    private readonly IHostEnvironment _environment;
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public StorageOverviewProvider(IHostEnvironment environment, MatmonRuntimeOptions runtimeOptions)
    {
        _environment = environment;
        _runtimeOptions = runtimeOptions;
    }

    public StorageOverview GetOverview()
    {
        var workspacePath = ResolveWorkspacePath();
        var dataPath = Path.GetDirectoryName(workspacePath)
            ?? Path.Combine(_environment.ContentRootPath, "data");
        var backupPath = ResolveBackupPath(dataPath);

        var telemetryPath = ResolveTelemetryPath(dataPath);

        var scan = ProbeStorageScanner.ScanDirectory(dataPath);
        var workspaceFileBytes = GetFileSize(workspacePath);
        var backupFileBytes = GetPathSize(backupPath);
        var telemetryFileBytes = GetTelemetrySize(telemetryPath);
        var drive = ProbeStorageScanner.ResolveDrive(dataPath);

        return new StorageOverview(
            workspacePath,
            dataPath,
            backupPath,
            telemetryPath,
            File.Exists(workspacePath),
            Directory.Exists(dataPath),
            File.Exists(telemetryPath),
            scan.TotalBytes,
            scan.FileCount,
            workspaceFileBytes,
            backupFileBytes,
            telemetryFileBytes,
            drive?.Name ?? Path.GetPathRoot(dataPath) ?? string.Empty,
            drive?.TotalSize,
            drive?.AvailableFreeSpace,
            ProbeStorageScanner.CalculateFreePercent(drive),
            scan.ErrorMessage);
    }

    public ProbeStorageSnapshot GetSnapshot()
    {
        var overview = GetOverview();
        return new ProbeStorageSnapshot(
            overview.DataDirectoryExists,
            overview.DataDirectoryBytes,
            overview.DataFileCount,
            overview.DriveAvailableBytes,
            overview.DriveFreePercent,
            overview.ErrorMessage);
    }

    private string ResolveTelemetryPath(string dataPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(_runtimeOptions.TelemetryPath)
            ? Path.Combine(dataPath, "telemetry.db")
            : _runtimeOptions.TelemetryPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private static long? GetTelemetrySize(string path)
    {
        long total = 0;
        var found = false;

        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (GetFileSize(candidate) is long size)
            {
                total += size;
                found = true;
            }
        }

        return found ? total : null;
    }

    private string ResolveWorkspacePath()
    {
        var configuredPath = string.IsNullOrWhiteSpace(_runtimeOptions.WorkspacePath)
            ? "data/workspace.json"
            : _runtimeOptions.WorkspacePath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private string ResolveBackupPath(string dataPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(_runtimeOptions.BackupPath)
            ? Path.Combine(dataPath, "backups")
            : _runtimeOptions.BackupPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private static long? GetFileSize(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? GetPathSize(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return ProbeStorageScanner.ScanDirectory(path).TotalBytes;
            }

            return GetFileSize(path);
        }
        catch
        {
            return null;
        }
    }

}

public sealed record StorageOverview(
    string WorkspacePath,
    string DataPath,
    string BackupPath,
    string TelemetryPath,
    bool WorkspaceFileExists,
    bool DataDirectoryExists,
    bool TelemetryFileExists,
    long DataDirectoryBytes,
    int DataFileCount,
    long? WorkspaceFileBytes,
    long? BackupFileBytes,
    long? TelemetryFileBytes,
    string DriveName,
    long? DriveTotalBytes,
    long? DriveAvailableBytes,
    double? DriveFreePercent,
    string? ErrorMessage)
{
    public double DataDirectoryMegabytes => Math.Round(DataDirectoryBytes / 1024.0 / 1024.0, 2);

    public double? DriveAvailableGigabytes => DriveAvailableBytes.HasValue
        ? Math.Round(DriveAvailableBytes.Value / 1024.0 / 1024.0 / 1024.0, 2)
        : null;

    public double? DriveTotalGigabytes => DriveTotalBytes.HasValue
        ? Math.Round(DriveTotalBytes.Value / 1024.0 / 1024.0 / 1024.0, 2)
        : null;

    public string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "-";
        }

        var value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        var size = (double)value;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(size >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
