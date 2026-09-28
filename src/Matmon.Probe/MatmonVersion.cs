using System.Reflection;

namespace Matmon.Probe;

/// <summary>
/// Resolves the build version shown in the UI. CI bakes the real version into
/// the image via the <c>MATMON_VERSION</c> environment variable (release builds
/// look like <c>0.1.&lt;run&gt;-&lt;builddate&gt;</c>, dev builds like
/// <c>nightly-&lt;run&gt;-&lt;builddate&gt;</c>). When the variable is absent - a
/// plain local/dev run - we fall back to <c>local-&lt;builddate&gt;</c> derived
/// from the assembly's build timestamp.
///
/// In between sits the version baked INTO the entry assembly (<c>-p:MatmonVersion=…</c>, see
/// Directory.Build.props). That is the one that matters for the agent: it runs on a customer's machine where
/// no one sets the environment variable, and it has to know its own version exactly, because the auto-update
/// compares it with the instance's manifest.
/// </summary>
public static class MatmonVersion
{
    public const string EnvironmentVariable = "MATMON_VERSION";

    public static string Current { get; } = Resolve();

    /// <summary>The release channel inferred from the version string.</summary>
    public static string Channel { get; } = ResolveChannel(Current);

    private static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var baked = EntryAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MatmonVersion")?.Value;
        if (!string.IsNullOrWhiteSpace(baked))
        {
            return baked.Trim();
        }

        return $"local-{GetBuildTimestampUtc():yyyyMMdd-HHmm}";
    }

    private static string ResolveChannel(string version)
    {
        if (version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase))
        {
            return "Nightly";
        }

        if (version.StartsWith("local", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        return "Release";
    }

    // Null under some test runners, hence the fallback to this library.
    private static Assembly EntryAssembly() => Assembly.GetEntryAssembly() ?? typeof(MatmonVersion).Assembly;

    private static DateTime GetBuildTimestampUtc()
    {
        try
        {
            // The ENTRY assembly, not this one: since this class moved into Matmon.Probe, the executing assembly
            // is the shared library - the Host and the agent would both have reported the library's build time
            // instead of their own.
            //
            // Assembly.Location is ALWAYS empty in a single-file app (the agent), so there the process's own
            // executable is the build artefact. Not the other way round: for "dotnet Matmon.Host.dll" the
            // process path is dotnet.exe, whose date says nothing about this build.
            var location = EntryAssembly().Location;
            if (string.IsNullOrEmpty(location))
            {
                location = Environment.ProcessPath ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                return File.GetLastWriteTimeUtc(location);
            }
        }
        catch
        {
            // Fall through to the process start time below.
        }

        return DateTime.UtcNow;
    }
}
