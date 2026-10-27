using System.Diagnostics;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Tests;

/// <summary>Who keeps the program folder busy during an update - run for real on Windows (skipped elsewhere).</summary>
public sealed class WindowsFolderLocksTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matmon-locks-" + Guid.NewGuid().ToString("N"));

    public WindowsFolderLocksTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_process_holding_a_file_open_is_named()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(_folder, "held.txt");
        File.WriteAllText(file, "x");
        using var holder = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"$f=[IO.File]::Open('{file}','Open','Read','None'); Start-Sleep 30\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            string? holders = null;
            for (var attempt = 0; attempt < 40 && holders is null; attempt++)
            {
                Thread.Sleep(250);
                holders = WindowsFolderLocks.Describe(_folder);
            }

            Assert.NotNull(holders);
            Assert.Contains($"pid {holder.Id}", holders);
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Fact]
    public void A_process_running_from_the_folder_is_ended_and_nothing_else()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var copy = Path.Combine(_folder, "PING.EXE");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), copy);
        using var inside = Process.Start(new ProcessStartInfo(copy, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        using var outside = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Contains($"pid {inside.Id}", WindowsFolderLocks.Describe(_folder));

            WindowsFolderLocks.EndProcessesRunningFrom(_folder);

            Assert.True(inside.WaitForExit(10_000));
            Assert.False(outside.HasExited); // the same program from another folder is left alone
        }
        finally
        {
            outside.Kill();
            outside.WaitForExit();
        }
    }
}
