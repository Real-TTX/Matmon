using System.Text.Json;
using Matmon.Host.Pages;
using Matmon.Host.Services;
using Matmon.Host.Services.SelfUpdate;
using Matmon.Probe;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>A remote Docker probe updated from its primary: the version rule, the command queue that rides the heartbeat
/// answer, the backward-compatible wire shapes, the probe's outcome file and its refusals.</summary>
public class ProbeUpdateTests
{
    // --- the version rule ---

    [Theory]
    [InlineData("nightly-12-20261029-1000", "nightly-11-20261028-1000", true)]
    [InlineData("0.9.40-20261029-1000", "nightly-11-20261028-1000", true)] // a downgrade too: the probe follows exactly
    [InlineData("nightly-12-20261029-1000", "NIGHTLY-12-20261029-1000", false)]
    [InlineData("nightly-12-20261029-1000", null, false)] // never reported a version
    [InlineData(null, "nightly-12", false)]
    public void An_update_is_offered_when_the_versions_differ(string? primary, string? probe, bool offered) =>
        Assert.Equal(offered, ProbeUpdatePolicy.ShouldOffer(primary, probe));

    [Theory]
    [InlineData("nightly-12-20261029-1000", true)]
    [InlineData("0.9.40-20261029-1000", true)]
    [InlineData("local-20261029-1000", false)] // a local build has no published tag
    [InlineData("latest:evil", false)]
    [InlineData("../x", false)]
    [InlineData("", false)]
    public void Only_a_published_version_is_pulled_by_its_tag(string version, bool published) =>
        Assert.Equal(published, ProbeUpdatePolicy.IsPublished(version));

    [Fact]
    public void A_probe_refuses_what_it_cannot_do()
    {
        var command = new ProbeUpdateCommand("c1", "nightly-12");
        Assert.Contains("no socket here", ProbeUpdatePolicy.WhyNot(command, "nightly-11", false, "no socket here", false));
        Assert.Contains("already being installed", ProbeUpdatePolicy.WhyNot(command, "nightly-11", true, null, true));
        Assert.Contains("already runs", ProbeUpdatePolicy.WhyNot(command, "nightly-12", true, null, false));
        Assert.Contains("not a valid image tag", ProbeUpdatePolicy.WhyNot(command with { Version = "a b" }, "nightly-11", true, null, false));
        Assert.Null(ProbeUpdatePolicy.WhyNot(command, "nightly-11", true, null, false));
    }

    // --- the command queue on the primary ---

    [Fact]
    public void A_command_rides_every_beat_until_the_probe_answers()
    {
        var commands = new ProbeUpdateCommands(new ProbeUpdateClock(DateTimeOffset.UtcNow));
        Assert.Null(commands.Queue("probe-01", "nightly-12", "admin"));

        var first = commands.OnHeartbeat("probe-01", "nightly-11", null);
        var again = commands.OnHeartbeat("probe-01", "nightly-11", new ProbeSelfUpdateReport(true));
        Assert.NotNull(first);
        Assert.Equal(first, again); // the same id - the probe ignores a repeat
        Assert.Null(commands.OnHeartbeat("probe-02", "nightly-11", null)); // only its own probe

        var answer = new ProbeSelfUpdateReport(true, CommandId: first!.Id, CommandAccepted: true, CommandMessage: "installing nightly-12");
        Assert.Null(commands.OnHeartbeat("probe-01", "nightly-11", answer));
        Assert.Equal(true, commands.State("probe-01")!.Accepted);
        Assert.True(commands.IsUpdating("probe-01", "nightly-11"));
        Assert.False(commands.IsUpdating("probe-01", "nightly-12"));
    }

    [Fact]
    public void One_command_at_a_time_per_probe()
    {
        var commands = new ProbeUpdateCommands(new ProbeUpdateClock(DateTimeOffset.UtcNow));
        Assert.Null(commands.Queue("probe-01", "nightly-12", "admin"));
        Assert.NotNull(commands.Queue("probe-01", "nightly-12", "admin"));
        Assert.Null(commands.Queue("probe-02", "nightly-12", "admin"));
    }

    [Fact]
    public void A_refusal_is_recorded_and_frees_the_probe_for_another_try()
    {
        var commands = new ProbeUpdateCommands(new ProbeUpdateClock(DateTimeOffset.UtcNow));
        commands.Queue("probe-01", "nightly-12", "admin");
        var command = commands.OnHeartbeat("probe-01", "nightly-11", null)!;
        commands.OnHeartbeat("probe-01", "nightly-11", new ProbeSelfUpdateReport(false, "no socket", CommandId: command.Id, CommandAccepted: false, CommandMessage: "no socket"));

        var state = commands.State("probe-01")!;
        Assert.Equal(false, state.Accepted);
        Assert.Equal("no socket", state.Message);
        Assert.Null(commands.Queue("probe-01", "nightly-12", "admin"));
    }

    [Fact]
    public void A_probe_that_reports_the_target_version_is_done_even_without_an_answer()
    {
        var commands = new ProbeUpdateCommands(new ProbeUpdateClock(DateTimeOffset.UtcNow));
        commands.Queue("probe-01", "nightly-12", "admin");
        commands.OnHeartbeat("probe-01", "nightly-11", null);

        // The new container beats before the old one's answer ever arrived.
        Assert.Null(commands.OnHeartbeat("probe-01", "nightly-12", new ProbeSelfUpdateReport(true)));
        Assert.Equal(true, commands.State("probe-01")!.Accepted);
    }

    [Fact]
    public void A_command_nobody_takes_is_given_up()
    {
        var clock = new ProbeUpdateClock(DateTimeOffset.UtcNow);
        var commands = new ProbeUpdateCommands(clock);
        commands.Queue("probe-01", "nightly-12", "admin");
        clock.Advance(ProbeUpdateCommands.AnswerTimeout);

        Assert.Equal(false, commands.State("probe-01")!.Accepted);
        Assert.Null(commands.OnHeartbeat("probe-01", "nightly-11", null));
        Assert.Null(commands.Queue("probe-01", "nightly-12", "admin")); // and it can be asked again
    }

    // --- the wire shapes ---

    [Fact]
    public void An_older_probe_heartbeat_still_reads_and_carries_no_report()
    {
        const string old = """{"probeId":"probe-01","probeName":"P","probeToken":"t","agentVersion":"nightly-11","heartbeatIntervalSeconds":30}""";
        var request = JsonSerializer.Deserialize<ProbeHeartbeatRequest>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("probe-01", request.ProbeId);
        Assert.Null(request.SelfUpdate);
    }

    [Fact]
    public void An_older_primary_answer_reads_as_no_command()
    {
        // What a primary before this returned: the probe's status snapshot.
        const string old = """{"probeId":"probe-01","probeName":"P","lastSeenUtc":"2026-10-29T10:00:00Z","state":"Online"}""";
        var answer = JsonSerializer.Deserialize<ProbeHeartbeatResponse>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(answer.InstallUpdate);
        Assert.Null(answer.PrimaryVersion);
    }

    [Fact]
    public void The_report_and_the_command_round_trip()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = new ProbeHeartbeatRequest("probe-01", "P", SelfUpdate: new ProbeSelfUpdateReport(true, Installing: true, InstallingTo: "nightly-12", CommandId: "c1", CommandAccepted: true));
        var back = JsonSerializer.Deserialize<ProbeHeartbeatRequest>(JsonSerializer.Serialize(request, web), web)!;
        Assert.Equal(request.SelfUpdate, back.SelfUpdate);

        var answer = new ProbeHeartbeatResponse("nightly-12", new ProbeUpdateCommand("c1", "nightly-12", "admin"));
        Assert.Equal(answer, JsonSerializer.Deserialize<ProbeHeartbeatResponse>(JsonSerializer.Serialize(answer, web), web));
    }

    [Fact]
    public void The_registry_keeps_the_report_for_the_probes_page()
    {
        var registry = new InMemoryProbeRegistry(new MatmonRuntimeOptions { HeartbeatIntervalSeconds = 30 });
        var snapshot = registry.Record(new ProbeHeartbeatRequest("probe-01", "P", AgentVersion: "nightly-11",
            SelfUpdate: new ProbeSelfUpdateReport(false, "the Docker socket is not mounted (/var/run/docker.sock)")), DateTimeOffset.UtcNow);
        Assert.False(snapshot.SelfUpdate!.CanUpdate);
    }

    // --- the probe side ---

    [Fact]
    public void The_outcome_file_keeps_the_newest_outcome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matmon-probe-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new ProbeUpdateOutcomeFile(Path.Combine(directory, "probe-last-update.json"));
            Assert.Null(file.Read());

            var now = DateTimeOffset.UtcNow;
            Assert.True(file.Write(new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, "nightly-11", "nightly-12", now, "did not come up")));
            Assert.False(file.Write(new SelfUpdateOutcome(SelfUpdateOutcomeKind.Updated, "nightly-10", "nightly-11", now.AddHours(-1), null)));

            var read = new ProbeUpdateOutcomeFile(file.Path).Read()!;
            Assert.Equal(SelfUpdateOutcomeKind.RolledBack, read.Kind);
            Assert.Equal("did not come up", read.Detail);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void A_probe_without_the_socket_refuses_once_and_ignores_the_repeat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matmon-probe-update-" + Guid.NewGuid().ToString("N"));
        var service = new ProbeSelfUpdateService(
            new DockerSelfUpdateTarget(Path.Combine(directory, "no-docker.sock"), "nightly-11", MatmonVersion.EnvironmentVariable),
            new ProbeUpdateOutcomeFile(Path.Combine(directory, "probe-last-update.json")),
            new SlaveProbeRuntimeState(),
            NullLogger<ProbeSelfUpdateService>.Instance,
            "nightly-11");

        var before = service.Report();
        Assert.False(before.CanUpdate);
        Assert.False(string.IsNullOrWhiteSpace(before.UnavailableReason));

        service.Handle(new ProbeUpdateCommand("c1", "nightly-12", "admin"));
        var after = service.Report();
        Assert.Equal("c1", after.CommandId);
        Assert.False(after.CommandAccepted);
        Assert.Contains("cannot update itself", after.CommandMessage);
        Assert.False(after.Installing);

        service.Handle(new ProbeUpdateCommand("c1", "nightly-12", "admin")); // the primary repeats until it sees the answer
        Assert.Equal(after, service.Report());
    }

    [Fact]
    public void A_rollback_ends_updating_at_once()
    {
        var clock = new ProbeUpdateClock(DateTimeOffset.UtcNow);
        var commands = new ProbeUpdateCommands(clock);
        commands.Queue("probe-01", "nightly-12", "admin");
        var command = commands.OnHeartbeat("probe-01", "nightly-11", null)!;
        commands.OnHeartbeat("probe-01", "nightly-11", new ProbeSelfUpdateReport(true, CommandId: command.Id, CommandAccepted: true));

        SystemProbeOverview Probe(ProbeSelfUpdateReport report) =>
            new(Guid.NewGuid(), "probe-01", "P", "Secondary", "Online", "", clock.GetUtcNow(), null, 0, Version: "nightly-11", SelfUpdate: report);

        Assert.True(ProbeUpdateView.Build(Probe(new ProbeSelfUpdateReport(true)), "nightly-12", commands).Updating);

        clock.Advance(TimeSpan.FromMinutes(1));
        var rolledBack = ProbeUpdateView.Build(Probe(new ProbeSelfUpdateReport(true, LastOutcomeKind: "RolledBack", LastOutcomeFrom: "nightly-11",
            LastOutcomeTo: "nightly-12", LastOutcomeUtc: clock.GetUtcNow(), LastOutcomeDetail: "did not come up")), "nightly-12", commands);
        Assert.False(rolledBack.Updating);
        Assert.True(rolledBack.OutcomeProblem);
        Assert.True(rolledBack.CanStart); // and it can be tried again
    }

    [Fact]
    public void The_probes_page_describes_the_outcome()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.StartsWith("Updated from nightly-11 to nightly-12",
            ProbeUpdateView.DescribeOutcome(new ProbeSelfUpdateReport(true, LastOutcomeKind: "Updated", LastOutcomeFrom: "nightly-11", LastOutcomeTo: "nightly-12", LastOutcomeUtc: at)));
        Assert.Contains("rolled back to nightly-11",
            ProbeUpdateView.DescribeOutcome(new ProbeSelfUpdateReport(true, LastOutcomeKind: "RolledBack", LastOutcomeFrom: "nightly-11", LastOutcomeTo: "nightly-12", LastOutcomeDetail: "no answer")));
        Assert.Null(ProbeUpdateView.DescribeOutcome(new ProbeSelfUpdateReport(true)));
    }
}

file sealed class ProbeUpdateClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
