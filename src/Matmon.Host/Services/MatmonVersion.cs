using System.Reflection;

namespace Matmon.Host.Services;

/// <summary>
/// Resolves the build version shown in the UI. CI bakes the real version into
/// the image via the <c>MATMON_VERSION</c> environment variable (release builds
/// look like <c>0.1.&lt;run&gt;-&lt;builddate&gt;</c>, dev builds like
/// <c>nightly-&lt;run&gt;-&lt;builddate&gt;</c>). When the variable is absent - a
/// plain local/dev run - we fall back to <c>local-&lt;builddate&gt;</c> derived
/// from the assembly's build timestamp.
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

    private static DateTime GetBuildTimestampUtc()
    {
        try
        {
            var location = Assembly.GetExecutingAssembly().Location;
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
