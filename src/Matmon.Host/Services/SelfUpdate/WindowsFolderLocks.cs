using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// Who keeps the program folder busy. Updating and reinstalling replace that folder as a whole, and Windows refuses while
/// any process runs from it or holds one of its files open - which used to end the update with nothing but "in use".
/// Shared by the updater and the setup (Matmon.Setup links this file).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsFolderLocks
{
    /// <summary>Ends every process whose program lies in <paramref name="folder"/> (an instance someone started by hand, a
    /// service process that has not quite exited yet) - never this process. Called once the service is stopped.</summary>
    public static void EndProcessesRunningFrom(string folder, Action<string>? log = null)
    {
        var root = Root(folder);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainModule?.FileName is not { } file || !Inside(file, root))
                    {
                        continue;
                    }

                    log?.Invoke($"ending {process.ProcessName} (pid {process.Id}), it runs from {folder}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Gone already, or a process we may not look at (it does not run from our folder then).
                }
            }
        }
    }

    /// <summary>The programs that run from <paramref name="folder"/> or hold one of its files open ("explorer.exe (pid 812),
    /// ..."), or null when none can be named - for the message of an update that could not replace the folder.</summary>
    public static string? Describe(string folder)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Root(folder);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } file && Inside(file, root))
                    {
                        names.Add($"{process.ProcessName} (pid {process.Id})");
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }

        foreach (var name in OpenFileHolders(folder))
        {
            names.Add(name);
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    // The Restart Manager names the processes holding a file open - what Explorer's "file in use" dialog uses.
    private static IEnumerable<string> OpenFileHolders(string folder)
    {
        string[] files;
        try
        {
            files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (files.Length == 0 || RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0)
        {
            return [];
        }

        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0)
            {
                return [];
            }

            uint needed = 0, count = 0, reasons = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ErrorMoreData || needed == 0)
            {
                return [];
            }

            var processes = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, processes, ref reasons) != 0)
            {
                return [];
            }

            return processes.Take((int)count)
                .Where(info => info.Process.ProcessId != Environment.ProcessId)
                .Select(info => $"{info.AppName} (pid {info.Process.ProcessId})")
                .ToList();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string Root(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

    private static bool Inside(string file, string root) => Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] files, uint applicationCount, IntPtr applications,
        uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded, ref uint processInfo, [In, Out] RmProcessInfo[]? affectedApps,
        ref uint rebootReasons);
}
