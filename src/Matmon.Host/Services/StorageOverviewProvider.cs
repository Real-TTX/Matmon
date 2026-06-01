using System.Globalization;

namespace Matmon.Host.Services;

public sealed class StorageOverviewProvider
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

        var scan = ScanDirectory(dataPath);
        var workspaceFileBytes = GetFileSize(workspacePath);
        var backupFileBytes = GetPathSize(backupPath);
        var drive = ResolveDrive(dataPath);

        return new StorageOverview(
            workspacePath,
            dataPath,
            backupPath,
            File.Exists(workspacePath),
            Directory.Exists(dataPath),
            scan.TotalBytes,
            scan.FileCount,
            workspaceFileBytes,
            backupFileBytes,
            drive?.Name ?? Path.GetPathRoot(dataPath) ?? string.Empty,
            drive?.TotalSize,
            drive?.AvailableFreeSpace,
            CalculateFreePercent(drive),
            scan.ErrorMessage);
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

    private static DirectoryScanResult ScanDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return new DirectoryScanResult(0, 0, "data directory does not exist");
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

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    pending.Push(directory);
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        return new DirectoryScanResult(totalBytes, fileCount, firstError);
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
                return ScanDirectory(path).TotalBytes;
            }

            return GetFileSize(path);
        }
        catch
        {
            return null;
        }
    }

    private static DriveInfo? ResolveDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
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

    private static double? CalculateFreePercent(DriveInfo? drive)
    {
        if (drive is null || drive.TotalSize <= 0)
        {
            return null;
        }

        return Math.Round((double)drive.AvailableFreeSpace / drive.TotalSize * 100.0, 2);
    }

    private sealed record DirectoryScanResult(long TotalBytes, int FileCount, string? ErrorMessage);
}

public sealed record StorageOverview(
    string WorkspacePath,
    string DataPath,
    string BackupPath,
    bool WorkspaceFileExists,
    bool DataDirectoryExists,
    long DataDirectoryBytes,
    int DataFileCount,
    long? WorkspaceFileBytes,
    long? BackupFileBytes,
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
