using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using Matmon.Host.Services.SelfUpdate;
using Matmon.Probe;

namespace Matmon.Setup;

internal enum PackageSource
{
    Embedded,
    File,
    Online
}

/// <summary>What the setup does, without a window - the wizard and the silent mode both call this.</summary>
internal static class SetupRunner
{
    public static string Version { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "MatmonVersion")?.Value ?? "local";

    public static string SelfPath => Environment.ProcessPath ?? throw new InvalidOperationException("the setup does not know its own file");

    /// <summary>
    /// Where the package comes from: appended to this file (the full setup), a zip given with /package, or - the online
    /// setup, which is the same program with nothing appended - downloaded from the release feed of its channel.
    /// </summary>
    public static PackageSource SourceOf(SetupArguments arguments)
    {
        if (arguments.PackagePath is not null)
        {
            return PackageSource.File;
        }

        using var self = File.OpenRead(SelfPath);
        return SetupPayload.Find(self) is not null ? PackageSource.Embedded : PackageSource.Online;
    }

    /// <summary>The feed the online setup downloads from: /feed=, else the channel of this setup's own build. A nightly
    /// setup installs the newest nightly, a release (and a local development build) the newest release.</summary>
    public static string FeedOf(SetupArguments arguments) =>
        WindowsUpdateFeed.FeedUrl(arguments.FeedUrl, Version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? "Nightly" : "Release")!;

    public static string ChannelName => Version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? "nightly" : "release";

    /// <summary>Installs (or updates). <paramref name="download"/> gets the downloaded share 0..1 while the online setup
    /// downloads; the version that ends up installed is returned.</summary>
    public static string Install(SetupArguments arguments, WindowsInstallOptions options, Action<string> progress, Action<double>? download = null)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"matmon-setup-{Guid.NewGuid():N}");
        var version = Version;
        try
        {
            switch (SourceOf(arguments))
            {
                case PackageSource.File:
                    progress("Unpacking Matmon ...");
                    ZipFile.ExtractToDirectory(arguments.PackagePath!, temporary);
                    break;

                case PackageSource.Embedded:
                    progress("Unpacking Matmon ...");
                    using (var self = File.OpenRead(SelfPath))
                    {
                        var (offset, length) = SetupPayload.Find(self)!.Value;
                        using var package = SetupPayload.OpenPackage(self, offset, length);
                        ZipFile.ExtractToDirectory(package, temporary);
                    }

                    break;

                default:
                    version = DownloadAndUnpack(arguments, temporary, progress, download);
                    break;
            }

            WindowsInstallation.Install(temporary, options, version, BareSetup(), progress);
            return version;
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string DownloadAndUnpack(SetupArguments arguments, string target, Action<string> progress, Action<double>? download)
    {
        var feed = FeedOf(arguments);
        var zip = Path.Combine(Path.GetTempPath(), $"matmon-setup-{Guid.NewGuid():N}.zip");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MatmonSetup/{Version}");
        try
        {
            progress($"Looking up the newest Matmon ({ChannelName} channel) ...");
            var manifest = WindowsUpdateFeed.ReadManifestAsync(client, feed, CancellationToken.None).GetAwaiter().GetResult();
            progress($"Downloading Matmon {manifest.Version} ({manifest.Size / 1048576.0:0} MB) ...");
            WindowsUpdateFeed.DownloadAsync(client, feed, manifest, zip, AgentPackageSignature.RequiredByThisBuild, download, CancellationToken.None)
                .GetAwaiter().GetResult();
            progress(string.IsNullOrWhiteSpace(manifest.Signature)
                ? "Download complete and checked (SHA-256; this build is not signed)."
                : "Download complete, checksum and signature are valid.");
            progress("Unpacking Matmon ...");
            ZipFile.ExtractToDirectory(zip, target);
            return manifest.Version;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Matmon could not be downloaded from {feed}: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                File.Delete(zip);
            }
            catch (IOException)
            {
            }
        }
    }

    public static void Uninstall(bool removeData, Action<string> progress) => WindowsInstallation.Uninstall(removeData, progress);

    /// <summary>Instance or probe, and the probe's connection - an installed probe keeps its stored one (read from its data
    /// folder, which needs the elevation this process has).</summary>
    public static (WindowsProbeConnection? Probe, string? Problem) ResolveProbe(SetupArguments arguments, WindowsInstallationInfo? installed) =>
        arguments.ResolveProbe(installed, installed?.Role == WindowsInstallationRole.Probe ? WindowsProbeSettings.ReadProbeSettings(installed.DataFolder) : null);

    /// <summary>For the tray icon: start / stop / restart the service (this process runs elevated).</summary>
    public static int ControlService(string action)
    {
        try
        {
            if (action is "stop" or "restart")
            {
                Log("Stopping the Matmon service (tray icon) ...");
                WindowsInstallation.StopService();
            }

            if (action is "start" or "restart")
            {
                Log("Starting the Matmon service (tray icon) ...");
                WindowsInstallation.StartService();
            }

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            Log($"FAILED to {action} the service: {ex.Message}");
            return 1;
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Runs this program again with administrator rights (one UAC prompt) and waits for it. The program itself starts
    /// without them (asInvoker) so that the tray icon, which is the same file, runs as the signed-in user.
    /// </summary>
    public static int RunElevated(string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SelfPath, string.Join(' ', args.Select(Quote)))
            {
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("the setup did not start");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223; // ERROR_CANCELLED: the UAC prompt was declined
        }
    }

    /// <summary>After an install by the elevated copy: the tray icon, started from here - as the signed-in user, not elevated.</summary>
    public static void StartTrayIcon()
    {
        if (File.Exists(WindowsInstallation.SetupProgramPath) && WindowsInstallation.TrayIconRegistered())
        {
            Process.Start(new ProcessStartInfo(WindowsInstallation.SetupProgramPath, "/tray") { UseShellExecute = false });
        }
    }

    // One argument for CreateProcess' command line (the CommandLineToArgvW rules: quotes, and backslashes before them, doubled).
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    /// <summary>This program without the package after it - what is installed as the uninstaller.</summary>
    private static byte[] BareSetup()
    {
        using var self = File.OpenRead(SelfPath);
        var length = SetupPayload.Find(self) is { } package ? package.Offset : self.Length;
        var bytes = new byte[length];
        self.Seek(0, SeekOrigin.Begin);
        self.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The uninstaller lives in the folder it removes, and a running program cannot delete itself: it copies itself to the
    /// temp folder and starts that copy instead. True = this process should end now.
    /// </summary>
    public static bool RelaunchOutsideSetupFolder(string[] args)
    {
        var self = Path.GetFullPath(SelfPath);
        if (!self.StartsWith(Path.GetFullPath(WindowsInstallation.SetupFolder), StringComparison.OrdinalIgnoreCase)
            && !self.StartsWith(Path.GetFullPath(WindowsInstallation.LegacySetupFolder), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var copy = Path.Combine(Path.GetTempPath(), $"Uninstall Matmon {Guid.NewGuid():N}.exe");
        File.Copy(self, copy);
        var start = new ProcessStartInfo(copy) { UseShellExecute = false };
        foreach (var arg in args.Append("/relaunched"))
        {
            start.ArgumentList.Add(arg);
        }

        Process.Start(start);
        return true;
    }

    /// <summary>A copy the uninstaller made of itself removes itself a moment after it has ended.</summary>
    public static void DeleteSelfLater()
    {
        if (!Path.GetFileName(SelfPath).StartsWith("Uninstall Matmon ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{SelfPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static string LogPath => Path.Combine(Path.GetTempPath(), "matmon-setup.log");

    public static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
