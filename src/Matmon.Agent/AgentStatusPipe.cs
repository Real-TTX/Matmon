using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Matmon.Probe;

namespace Matmon.Agent;

/// <summary>What the tray shows about the agent - deliberately without the probe token.</summary>
public sealed record AgentStatusSnapshot(
    string Version,
    string? ProbeName,
    string? ProbeId,
    string? PrimaryUrl,
    bool Connected,
    string StatusMessage,
    DateTimeOffset? LastHeartbeatUtc,
    int AssignedSensorCount,
    string? UpdateStatus,
    DateTimeOffset StartedUtc,
    string ConfigPath,
    string StateDirectory);

/// <summary>
/// The tray's window into the service: a local named pipe that answers one question - "status" - with a
/// JSON snapshot. The tray is a separate process in the user's session (a service runs in session 0 and
/// cannot show UI), so it needs a channel; a pipe is local-only by construction and needs no port.
///
/// Read-only on purpose: anything that CHANGES the agent (setup, re-enrolment) goes through an elevated
/// <c>setup</c> run, never through this pipe - otherwise every local user could re-point the agent. And the
/// snapshot carries no token, so any authenticated local user may read it.
/// </summary>
public sealed class AgentStatusPipeServer : BackgroundService
{
    public const string PipeName = "matmon-agent-status";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ProbeRuntimeOptions _options;
    private readonly SlaveProbeRuntimeState _runtimeState;
    private readonly AgentUpdateSettings _updateSettings;
    private readonly AgentConfigLocation _configLocation;
    private readonly ILogger<AgentStatusPipeServer> _logger;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    public AgentStatusPipeServer(
        ProbeRuntimeOptions options,
        SlaveProbeRuntimeState runtimeState,
        AgentUpdateSettings updateSettings,
        AgentConfigLocation configLocation,
        ILogger<AgentStatusPipeServer> logger)
    {
        _options = options;
        _runtimeState = runtimeState;
        _updateSettings = updateSettings;
        _configLocation = configLocation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                var request = await ReadLineAsync(reader, stoppingToken);
                if (string.Equals(request, "status", StringComparison.Ordinal))
                {
                    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(Snapshot(), Json));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A client that hangs up mid-answer is normal; anything else must not stop the agent.
                _logger.LogDebug(ex, "Status pipe request failed");
            }
        }
    }

    private AgentStatusSnapshot Snapshot()
    {
        var runtime = _runtimeState.Snapshot();
        return new AgentStatusSnapshot(
            MatmonVersion.Current,
            _options.ProbeName,
            _options.ProbeId,
            _options.PrimaryUrl,
            runtime.IsConnected,
            runtime.StatusMessage,
            runtime.LastHeartbeatUtc,
            runtime.AssignedSensorCount,
            _runtimeState.AgentUpdateStatus,
            _startedUtc,
            _configLocation.Path,
            _updateSettings.StateDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return (await reader.ReadLineAsync(timeout.Token))?.Trim();
    }
}

/// <summary>The status pipe's client - used by the tray.</summary>
public static class AgentStatusPipeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The running agent's status, or null when no agent answers (service stopped or not installed).</summary>
    public static async Task<AgentStatusSnapshot?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", AgentStatusPipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(2), cancellationToken);

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("status".AsMemory(), cancellationToken);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<AgentStatusSnapshot>(line, Json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Where this agent's config file is - shown by the tray so support knows where to look.</summary>
public sealed record AgentConfigLocation(string Path);
