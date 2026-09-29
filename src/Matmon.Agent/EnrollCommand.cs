using System.Net;
using System.Net.Http.Json;
using Matmon.Core.Domain;
using Matmon.Probe;

namespace Matmon.Agent;

/// <summary>
/// <c>matmon-agent enroll --url https://matmon.example --code K7QM-4TXC-9RHA-B2NW-P6FE [--force]</c>
///
/// Trades a one-time code from the instance's Agents page for a probe identity and writes it to the agent's
/// config file. Run once at install time, as administrator / root (the file is only readable by those).
/// The tray's setup dialog goes through <see cref="EnrollAsync"/> as well, so both paths enrol identically.
/// </summary>
public static class EnrollCommand
{
    public static async Task<int> RunAsync(string[] args, string configPath)
    {
        var url = ReadOption(args, "--url");
        var code = ReadOption(args, "--code");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("Usage: matmon-agent enroll --url <instance url> --code <enrolment code> [--force] [--config <path>]");
            Console.Error.WriteLine("Create the code under Agents on your Matmon instance.");
            return 2;
        }

        var result = await EnrollAsync(url, code, args.Contains("--force"), args.Contains("--allow-http"), configPath);
        (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
        return result.ExitCode;
    }

    public static async Task<EnrollResult> EnrollAsync(string url, string code, bool force, bool allowHttp, string configPath)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            return new EnrollResult(2, $"'{url}' is not an http(s) URL.");
        }

        // The answer carries the agent's lifelong credential. In the clear that is fine on a LAN, not across
        // the internet - the same rule the instance applies to its own cloud link.
        if (baseUri.Scheme == "http" && !CloudUrlPolicy.IsPlainHttpAllowed(baseUri) && !allowHttp)
        {
            return new EnrollResult(2,
                $"Refusing to enrol over plain http with a public host ({baseUri.Host}): the probe token would travel unencrypted. " +
                "Use https, or pass --allow-http if you really mean it.");
        }

        var existing = AgentConfigFile.ReadEnrolledProbeName(configPath);
        if (existing is not null && !force)
        {
            return new EnrollResult(3,
                $"This machine is already enrolled as '{existing}' ({configPath}). Pass --force to replace that identity with a new probe.");
        }

        var system = ProbeSystemInfoProvider.Collect();
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                "api/agents/enroll",
                new AgentEnrollRequest(code.Trim(), system.Host, system.OperatingSystem, MatmonVersion.Current));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new EnrollResult(4, $"Could not reach {baseUri}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new EnrollResult(5, DescribeFailure(response.StatusCode, await ReadErrorAsync(response)));
            }

            var enrolled = await response.Content.ReadFromJsonAsync<AgentEnrollResponse>();
            if (enrolled is null || string.IsNullOrWhiteSpace(enrolled.ProbeId) || string.IsNullOrWhiteSpace(enrolled.ProbeToken))
            {
                return new EnrollResult(5, "The instance answered, but not with an enrolment. Is the URL a Matmon instance?");
            }

            AgentConfigFile.Write(configPath, baseUri.ToString().TrimEnd('/'), enrolled.ProbeId, enrolled.ProbeToken, enrolled.ProbeName);
            return new EnrollResult(0,
                $"Enrolled as probe '{enrolled.ProbeName}' ({enrolled.ProbeId}). Identity written to {configPath}. Start (or restart) the Matmon Agent service.",
                enrolled.ProbeName);
        }
    }

    private static string DescribeFailure(HttpStatusCode status, string? error) => (status, error) switch
    {
        (HttpStatusCode.BadRequest, "invalid_code") => "The code is wrong, already used or expired. Create a new one under Agents on the instance.",
        (HttpStatusCode.Forbidden, "probe_limit") => "The instance's licence allows no further probes.",
        (HttpStatusCode.TooManyRequests, _) => "Too many enrolment attempts from this address. Wait a minute and try again.",
        (HttpStatusCode.NotFound, _) => "The instance has no enrolment endpoint - it is either not a Matmon primary or older than this agent.",
        _ => $"Enrolment failed: {(int)status} {status}{(string.IsNullOrWhiteSpace(error) ? string.Empty : $" ({error})")}."
    };

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
            return body?.Error;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed record ErrorBody(string? Error);
}

public sealed record EnrollResult(int ExitCode, string Message, string? ProbeName = null)
{
    public bool Success => ExitCode == 0;
}
