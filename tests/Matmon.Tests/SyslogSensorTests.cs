using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The two syslog sensors against a real log store: what they count, which state that makes, and the cases where saying
/// "healthy" would be a lie (receiver off, a device that never sent anything). Plus the rule that decides WHERE they run -
/// on the primary, whatever probe they sit under, because only the primary has the log.
/// </summary>
public sealed class SyslogSensorTests : IDisposable
{
    private const string Switch = "192.168.1.2";

    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-syslogsensor-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _workspace;
    private readonly SyslogStore _log;
    private readonly SyslogMessagesSensorExecutor _messages;
    private readonly SyslogMatchSensorExecutor _match;

    public SyslogSensorTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _workspace = new InMemoryMonitoringWorkspaceStore(
            new SyslogSensorHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        _log = new SyslogStore(Path.Combine(_dir, "syslog.db"), NullLogger<SyslogStore>.Instance);
        _messages = new SyslogMessagesSensorExecutor(_log, _workspace);
        _match = new SyslogMatchSensorExecutor(_log, _workspace);
    }

    public void Dispose()
    {
        _log.Dispose();
        _workspace.Dispose();
        _telemetry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup must not fail the run
        }
    }

    private void ReceiverOn() => _workspace.UpdateSyslogSettings(new SyslogSettings { Enabled = true });

    private async Task Log(params SyslogMessage[] messages)
    {
        foreach (var message in messages)
        {
            Assert.True(_log.TryEnqueue(message));
        }

        await _log.FlushAsync();
    }

    private static SyslogMessage Msg(string source, int severity, string text, TimeSpan? ago = null, string? hostname = null) => new()
    {
        ReceivedUtc = DateTimeOffset.UtcNow - (ago ?? TimeSpan.FromMinutes(5)),
        SourceAddress = source,
        Severity = severity,
        Hostname = hostname,
        Message = text,
        Format = SyslogFormat.Rfc3164
    };

    private static SensorExecutionContext MessagesContext(string target, Action<MonitoringSettings>? configure = null) =>
        Context(SyslogSensorTypes.Messages, target, configure);

    private static SensorExecutionContext MatchContext(string target, string pattern, Action<MonitoringSettings>? configure = null) =>
        Context(SyslogSensorTypes.Match, target, settings =>
        {
            settings.Parameters["syslog.pattern"] = pattern;
            configure?.Invoke(settings);
        });

    private static SensorExecutionContext Context(string type, string target, Action<MonitoringSettings>? configure)
    {
        var settings = new MonitoringSettings();
        configure?.Invoke(settings);
        return new SensorExecutionContext(type, target, settings);
    }

    private static double Channel(SensorExecutionResult result, string key) =>
        result.Channels.Single(channel => channel.Key == key).Value ?? double.NaN;

    // ---- Syslog Messages ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task MessagesCountsErrorsWarningsAndEverythingInTheWindow()
    {
        ReceiverOn();
        await Log(
            Msg(Switch, SyslogSeverity.Error, "link down"),
            Msg(Switch, SyslogSeverity.Emergency, "kernel panic"),
            Msg(Switch, SyslogSeverity.Warning, "fan slow"),
            Msg(Switch, SyslogSeverity.Informational, "login ok"),
            Msg(Switch, SyslogSeverity.Informational, "login ok"),
            Msg(Switch, SyslogSeverity.Debug, "tick"),
            Msg(Switch, SyslogSeverity.Error, "a year-old problem", ago: TimeSpan.FromHours(2)),
            Msg("192.168.1.99", SyslogSeverity.Error, "somebody else's error"));

        var result = await _messages.ExecuteAsync(MessagesContext(Switch));

        Assert.Equal(SensorState.Healthy, result.State); // no thresholds set: counting is not judging
        Assert.Equal("errors", result.DefaultChannelKey);
        Assert.Equal(2, Channel(result, "errors"));
        Assert.Equal(1, Channel(result, "warnings"));
        Assert.Equal(6, Channel(result, "messages"));
        Assert.Contains("2 error(s)", result.Message);
        Assert.Contains("Latest error:", result.Message);
    }

    [Fact]
    public async Task TheDefaultThresholdsTurnErrorsIntoAWarningAndThenACritical()
    {
        ReceiverOn();
        void Defaults(MonitoringSettings settings) => SensorThresholdDefaults.Apply(SyslogSensorTypes.Messages, settings);

        await Log(Msg(Switch, SyslogSeverity.Informational, "all quiet"));
        Assert.Equal(SensorState.Healthy, (await _messages.ExecuteAsync(MessagesContext(Switch, Defaults))).State);

        await Log(Msg(Switch, SyslogSeverity.Error, "link down"));
        Assert.Equal(SensorState.Warning, (await _messages.ExecuteAsync(MessagesContext(Switch, Defaults))).State);

        await Log(Enumerable.Range(0, 9).Select(index => Msg(Switch, SyslogSeverity.Error, $"link down {index}")).ToArray());
        Assert.Equal(SensorState.Critical, (await _messages.ExecuteAsync(MessagesContext(Switch, Defaults))).State);
    }

    [Fact]
    public async Task ADeviceThatNeverSentAnythingIsUnknownNotHealthy()
    {
        ReceiverOn();
        await Log(Msg("192.168.1.99", SyslogSeverity.Informational, "someone else"));

        var result = await _messages.ExecuteAsync(MessagesContext(Switch));

        Assert.Equal(SensorState.Unknown, result.State);
        Assert.Contains($"Nothing from {Switch}", result.Message);
        Assert.Empty(result.Channels);
    }

    [Fact]
    public async Task ADeviceThatWentQuietShowsHowLongAgoAndCanBeJudgedBySilence()
    {
        ReceiverOn();
        await Log(Msg(Switch, SyslogSeverity.Error, "the last thing it said", ago: TimeSpan.FromHours(3)));

        static void SilenceMeansWarning(MonitoringSettings settings) =>
            MonitoringSettings.SetChannelThreshold(settings, "lastMessageAgeSeconds", "warning", new ThresholdRule(ThresholdDirection.Above, 7200));

        var plain = await _messages.ExecuteAsync(MessagesContext(Switch));
        Assert.Equal(SensorState.Healthy, plain.State);
        Assert.Equal(0, Channel(plain, "messages"));
        Assert.InRange(Channel(plain, "lastMessageAgeSeconds"), 3 * 3600 - 120, 3 * 3600 + 600);
        Assert.Contains("No messages", plain.Message);

        var judged = await _messages.ExecuteAsync(MessagesContext(Switch, SilenceMeansWarning));
        Assert.Equal(SensorState.Warning, judged.State);
    }

    [Fact]
    public async Task WithTheReceiverOffTheSensorSaysItCannotKnow()
    {
        await Log(Msg(Switch, SyslogSeverity.Informational, "old news")); // the log may still hold messages from before it was switched off

        var result = await _messages.ExecuteAsync(MessagesContext(Switch));

        Assert.Equal(SensorState.Unknown, result.State);
        Assert.Contains("switched off", result.Message);
    }

    [Fact]
    public async Task WithoutAnythingToLookForTheSensorSaysSo()
    {
        ReceiverOn();

        var result = await _messages.ExecuteAsync(MessagesContext(target: ""));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("No sender", result.Message);
    }

    [Fact]
    public async Task TheSenderParameterWinsAndMatchesHostNamesAndSeveralSenders()
    {
        ReceiverOn();
        await Log(
            Msg("10.0.0.9", SyslogSeverity.Error, "from the named one", hostname: "sw-core"),
            Msg("10.0.0.10", SyslogSeverity.Error, "from the second"),
            Msg("10.0.0.11", SyslogSeverity.Error, "from nobody we asked about"));

        var byName = await _messages.ExecuteAsync(MessagesContext("ignored-target", settings => settings.Parameters["syslog.source"] = "SW-CORE"));
        Assert.Equal(1, Channel(byName, "errors"));

        var several = await _messages.ExecuteAsync(MessagesContext("ignored-target", settings => settings.Parameters["syslog.source"] = "sw-core, 10.0.0.10"));
        Assert.Equal(2, Channel(several, "errors"));
    }

    [Fact]
    public async Task IgnoredPhrasesAreLeftOutOfTheCount()
    {
        ReceiverOn();
        await Log(
            Msg(Switch, SyslogSeverity.Error, "spanning-tree topology change on VLAN 10"),
            Msg(Switch, SyslogSeverity.Error, "SPANNING-TREE root changed"),
            Msg(Switch, SyslogSeverity.Error, "interface Gi1/0/5 down"));

        var result = await _messages.ExecuteAsync(MessagesContext(Switch, settings => settings.Parameters["syslog.exclude"] = "spanning-tree\r\n"));

        Assert.Equal(1, Channel(result, "errors"));
        Assert.Equal(1, Channel(result, "messages"));
    }

    [Fact]
    public async Task TheWindowCanBeWidened()
    {
        ReceiverOn();
        await Log(Msg(Switch, SyslogSeverity.Error, "three hours ago", ago: TimeSpan.FromHours(3)));

        var narrow = await _messages.ExecuteAsync(MessagesContext(Switch));
        var wide = await _messages.ExecuteAsync(MessagesContext(Switch, settings => settings.Parameters["syslog.window"] = "360"));

        Assert.Equal(0, Channel(narrow, "errors"));
        Assert.Equal(1, Channel(wide, "errors"));
        Assert.Contains("6 h", wide.Message);
    }

    [Fact]
    public async Task AHostNameIsResolvedToTheAddressTheLogRecords()
    {
        ReceiverOn();
        // A host entered by name in the tree, a device that reports its address and no name: the name has to find it.
        await Log(
            Msg("127.0.0.1", SyslogSeverity.Error, "from loopback v4"),
            Msg("::1", SyslogSeverity.Error, "from loopback v6"));

        var result = await _messages.ExecuteAsync(MessagesContext("localhost"));

        Assert.NotEqual(SensorState.Unknown, result.State);
        Assert.True(Channel(result, "messages") >= 1, "localhost resolves to at least one loopback address");
    }

    [Fact]
    public async Task AHostNameThatDoesNotResolveStillMatchesByName()
    {
        ReceiverOn();
        await Log(Msg("10.0.0.9", SyslogSeverity.Error, "named in the message", hostname: "no-such-host.invalid"));

        var result = await _messages.ExecuteAsync(MessagesContext("no-such-host.invalid"));

        Assert.Equal(1, Channel(result, "errors"));
    }

    // ---- Syslog Match -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MatchFindsAPhraseWithoutCaringAboutCase()
    {
        ReceiverOn();
        await Log(
            Msg(Switch, SyslogSeverity.Error, "%LINK-3-UPDOWN: Interface Gi1/0/5, changed state to down"),
            Msg(Switch, SyslogSeverity.Error, "%LINK-3-UPDOWN: Interface Gi1/0/5, changed state to up"),
            Msg("192.168.1.99", SyslogSeverity.Error, "Interface Gi1/0/1, changed state to down"));

        var result = await _match.ExecuteAsync(MatchContext(Switch, "CHANGED STATE TO DOWN", settings =>
            SensorThresholdDefaults.Apply(SyslogSensorTypes.Match, settings)));

        Assert.Equal(1, Channel(result, "matches"));
        Assert.Equal("matches", result.DefaultChannelKey);
        Assert.Equal(SensorState.Warning, result.State); // the default: the first hit is worth a look
        Assert.Contains("Latest (", result.Message);
        Assert.Contains("Gi1/0/5", result.Message);
        Assert.Contains(result.Channels, channel => channel.Key == "lastMatchAgeSeconds");
    }

    [Fact]
    public async Task MatchCanTakeARegularExpression()
    {
        ReceiverOn();
        await Log(
            Msg(Switch, SyslogSeverity.Error, "port Gi1/0/5 flapping"),
            Msg(Switch, SyslogSeverity.Error, "port Gi1/0/7 flapping"),
            Msg(Switch, SyslogSeverity.Error, "port Gi1/0/9 flapping"));

        var result = await _match.ExecuteAsync(MatchContext(Switch, @"Gi1/0/(5|7) ", settings => settings.Parameters["syslog.mode"] = "regex"));

        Assert.Equal(2, Channel(result, "matches"));
    }

    [Fact]
    public async Task ABrokenRegularExpressionIsReportedNotSwallowed()
    {
        ReceiverOn();

        var result = await _match.ExecuteAsync(MatchContext(Switch, "(unclosed", settings => settings.Parameters["syslog.mode"] = "regex"));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.StartsWith("The regular expression is not valid", result.Message);
    }

    [Fact]
    public async Task MatchCanBeNarrowedToImportantMessages()
    {
        ReceiverOn();
        await Log(
            Msg(Switch, SyslogSeverity.Informational, "login failed for user x"),
            Msg(Switch, SyslogSeverity.Error, "login failed for user y"),
            Msg(Switch, SyslogSeverity.Warning, "login failed for user z"));

        var any = await _match.ExecuteAsync(MatchContext(Switch, "login failed"));
        var errors = await _match.ExecuteAsync(MatchContext(Switch, "login failed", settings => settings.Parameters["syslog.minSeverity"] = "error"));
        var warnings = await _match.ExecuteAsync(MatchContext(Switch, "login failed", settings => settings.Parameters["syslog.minSeverity"] = "warning"));

        Assert.Equal(3, Channel(any, "matches"));
        Assert.Equal(1, Channel(errors, "matches"));
        Assert.Equal(2, Channel(warnings, "matches"));
    }

    [Fact]
    public async Task NoHitIsHealthyAndSaysWhatItLookedFor()
    {
        ReceiverOn();
        await Log(Msg(Switch, SyslogSeverity.Informational, "all quiet"));

        var result = await _match.ExecuteAsync(MatchContext(Switch, "kernel panic"));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(0, Channel(result, "matches"));
        Assert.DoesNotContain(result.Channels, channel => channel.Key == "lastMatchAgeSeconds");
        Assert.Contains("kernel panic", result.Message);
    }

    [Fact]
    public async Task MatchNeedsSomethingToLookFor()
    {
        ReceiverOn();

        var result = await _match.ExecuteAsync(Context(SyslogSensorTypes.Match, Switch, configure: null));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("Looking for", result.Message);
    }

    [Fact]
    public async Task MatchWithTheReceiverOffIsUnknown()
    {
        var result = await _match.ExecuteAsync(MatchContext(Switch, "anything"));

        Assert.Equal(SensorState.Unknown, result.State);
        Assert.Contains("switched off", result.Message);
    }

    // ---- what the catalog and the defaults say ------------------------------------------------------------------------

    [Fact]
    public void BothSensorsAreOfferedAndEveryRegistryKnowsThem()
    {
        foreach (var key in new[] { SyslogSensorTypes.Messages, SyslogSensorTypes.Match })
        {
            Assert.Contains(SensorDefinitionCatalog.BuiltIns, definition => definition.Key == key);
            Assert.Equal("Logs", SensorTypeCategories.Resolve(key));
            Assert.Equal(TimeSpan.FromMinutes(1), SensorScheduleDefaults.Resolve(key));
            Assert.Same(SensorTelemetryProfiles.Responsive, SensorTelemetryProfiles.Resolve(key));
        }

        Assert.Contains("Logs", SensorTypeCategories.Order);
        Assert.True(SensorThresholdDefaults.TryResolve(SyslogSensorTypes.Messages, "errors", "warning", out var warning));
        Assert.Equal(ThresholdDirection.Above, warning.Direction);
        Assert.True(SensorThresholdDefaults.TryResolve(SyslogSensorTypes.Messages, "errors", "critical", out _));
        Assert.True(SensorThresholdDefaults.TryResolve(SyslogSensorTypes.Match, "matches", "warning", out _));
        Assert.False(SensorThresholdDefaults.TryResolve(SyslogSensorTypes.Messages, "lastMessageAgeSeconds", "warning", out _));
    }

    [Fact]
    public void TheDefinitionsCarryTheParametersTheExecutorsRead()
    {
        var messageKeys = SyslogMessagesSensorExecutor.Definition.Parameters.Select(parameter => parameter.Key).ToArray();
        Assert.Equal(["syslog.source", "syslog.window", "syslog.exclude"], messageKeys);

        var matchKeys = SyslogMatchSensorExecutor.Definition.Parameters.Select(parameter => parameter.Key).ToArray();
        Assert.Equal(["syslog.pattern", "syslog.mode", "syslog.minSeverity", "syslog.source", "syslog.window"], matchKeys);
        Assert.True(SyslogMatchSensorExecutor.Definition.Parameters.Single(parameter => parameter.Key == "syslog.pattern").Required);
    }

    // ---- where they run -----------------------------------------------------------------------------------------------

    private (ProbeElement Probe, HostElement Host) RemoteProbeWithAHost()
    {
        var rootId = _workspace.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;
        var probe = _workspace.CreateProbe(rootId, "Branch office", null);
        var host = _workspace.CreateHost(probe.Id, "sw-core", Switch, null);
        return (probe, host);
    }

    [Theory]
    [InlineData("syslog-messages")]
    [InlineData("syslog-match")]
    [InlineData("SYSLOG-MATCH")]
    [InlineData("probe-heartbeat")]
    [InlineData(" syslog-messages ")]
    public void SensorsThatReadThePrimarysOwnStateRunOnThePrimaryOnly(string key) =>
        Assert.True(SensorPlacement.RunsOnPrimaryOnly(key));

    [Theory]
    [InlineData("ping")]
    [InlineData("http")]
    [InlineData("local-health")]
    [InlineData("windows-health")]
    [InlineData("probe-health")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseRunsWhereItIsMeasuredFrom(string? key) =>
        Assert.False(SensorPlacement.RunsOnPrimaryOnly(key));

    [Fact]
    public void ASyslogSensorUnderARemoteProbeIsPolledByThePrimary()
    {
        var (probe, host) = RemoteProbeWithAHost();
        var syslog = _workspace.CreateSensor(host.Id, "Switch log", SyslogSensorTypes.Messages, string.Empty, null);
        var ping = _workspace.CreateSensor(host.Id, "Switch ping", "ping", string.Empty, null);
        var elements = _workspace.GetAllElements().ToDictionary(element => element.Id);

        Assert.True(SensorPollingService.IsLocalSensor(elements[syslog.Id], elements));
        Assert.False(SensorPollingService.IsLocalSensor(elements[ping.Id], elements)); // the probe measures this one itself
        Assert.NotNull(probe);
    }

    [Fact]
    public void ASyslogSensorIsNeverHandedToTheProbeItSitsUnder()
    {
        var (probe, host) = RemoteProbeWithAHost();
        var syslog = _workspace.CreateSensor(host.Id, "Switch log", SyslogSensorTypes.Match, string.Empty, null);
        var ping = _workspace.CreateSensor(host.Id, "Switch ping", "ping", string.Empty, null);
        var provider = new ProbeSensorAssignmentProvider(_workspace);

        var assigned = provider.BuildAssignments(probe.ProbeId).Sensors.Select(sensor => sensor.SensorId).ToArray();

        Assert.Contains(ping.Id, assigned);
        Assert.DoesNotContain(syslog.Id, assigned);

        // ...and a probe cannot record a result for it either: the primary alone holds the log it would be reporting on.
        Assert.False(provider.TryBuildRecordingContext(probe.ProbeId, syslog.Id, out _, out _, out _));
        Assert.True(provider.TryBuildRecordingContext(probe.ProbeId, ping.Id, out _, out _, out _));
    }

    [Fact]
    public void RunNowOnASyslogSensorRunsOnThePrimaryNotOnTheProbe()
    {
        var (probe, host) = RemoteProbeWithAHost();
        var syslog = _workspace.CreateSensor(host.Id, "Switch log", SyslogSensorTypes.Messages, string.Empty, null);
        var ping = _workspace.CreateSensor(host.Id, "Switch ping", "ping", string.Empty, null);
        var provider = new ProbeSensorAssignmentProvider(_workspace);

        Assert.True(provider.TryBuildProbeReadyRun(syslog.Id, out var syslogOwner, out var syslogType, out var syslogTarget, out _));
        Assert.Null(syslogOwner);
        Assert.Equal(SyslogSensorTypes.Messages, syslogType);
        Assert.Equal(Switch, syslogTarget); // still resolved from the host it sits under

        Assert.True(provider.TryBuildProbeReadyRun(ping.Id, out var pingOwner, out _, out _, out _));
        Assert.Equal(probe.ProbeId, pingOwner);
    }

    [Fact]
    public void TheOwningProbeOfAnElementIgnoresPrimaryOnlyTypes()
    {
        var (probe, host) = RemoteProbeWithAHost();

        Assert.Equal(probe.ProbeId, _workspace.ResolveOwningRemoteProbeId(host.Id));
        Assert.Equal(probe.ProbeId, _workspace.ResolveOwningRemoteProbeId(host.Id, "ping"));
        Assert.Null(_workspace.ResolveOwningRemoteProbeId(host.Id, SyslogSensorTypes.Messages));
        Assert.Null(_workspace.ResolveOwningRemoteProbeId(host.Id, SyslogSensorTypes.Match));
    }
}

file sealed class SyslogSensorHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
