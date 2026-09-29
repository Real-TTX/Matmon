using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

// The enrolment code is the one anonymous way into a probe token, so what matters is what it must NOT do:
// work twice, work late, survive a restart in the clear, or ride along in a backup.
public sealed class AgentEnrollmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-enroll-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;

    public AgentEnrollmentTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _store = NewStore();
    }

    private InMemoryMonitoringWorkspaceStore NewStore() =>
        new(new EnrollmentHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private static AgentEnrollmentRedemption? Redeem(InMemoryMonitoringWorkspaceStore store, string? code, string? host) =>
        store.RedeemAgentEnrollment(code, host, allowNewProbe: true).Redemption;

    private ProbeElement RemoteProbe(string name)
    {
        var rootId = _store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;
        return _store.CreateProbe(rootId, name, null);
    }

    [Fact]
    public void AReenrolmentCodeHandsTheExistingProbeANewToken()
    {
        var probe = RemoteProbe("Docker probe 01");
        var oldToken = probe.EnrollmentToken!;
        var probesBefore = _store.GetAllElements().OfType<ProbeElement>().Count();
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", probe.Id);

        Assert.Equal("Docker probe 01", issue.Enrollment.Name);

        var redeemed = Redeem(_store, issue.Code, "SOME-OTHER-HOSTNAME");

        Assert.NotNull(redeemed);
        Assert.True(redeemed.Reenrolled);
        Assert.Equal(probe.Id, redeemed.ElementId);
        Assert.Equal(probe.ProbeId, redeemed.ProbeId);
        Assert.Equal("Docker probe 01", redeemed.ProbeName);
        Assert.Equal(probesBefore, _store.GetAllElements().OfType<ProbeElement>().Count());

        // The install it takes over from is locked out; the agent is in.
        Assert.False(_store.TryValidateProbe(probe.ProbeId, oldToken));
        Assert.True(_store.TryValidateProbe(probe.ProbeId, redeemed.ProbeToken));
        Assert.NotNull(((ProbeElement)_store.FindElement(probe.Id)!).AgentEnrolledUtc);
    }

    [Fact]
    public void AFullLicenceStopsANewProbeButNotAReenrolment()
    {
        var probe = RemoteProbe("Existing");
        var newProbeCode = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);
        var reenrolCode = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, probe.Id);

        var refused = _store.RedeemAgentEnrollment(newProbeCode.Code, "x", allowNewProbe: false);
        Assert.Equal(AgentEnrollmentStatus.ProbeLimit, refused.Status);
        Assert.True(_store.IsAgentEnrollmentCodeValid(newProbeCode.Code)); // not burnt by the refusal

        var reenrolled = _store.RedeemAgentEnrollment(reenrolCode.Code, "x", allowNewProbe: false);
        Assert.Equal(AgentEnrollmentStatus.Enrolled, reenrolled.Status);
    }

    [Fact]
    public void ACodeForADeletedProbeIsDead()
    {
        var probe = RemoteProbe("Gone soon");
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, probe.Id);
        _store.DeleteElement(probe.Id);

        Assert.Equal(AgentEnrollmentStatus.Invalid, _store.RedeemAgentEnrollment(issue.Code, "x", allowNewProbe: true).Status);
        Assert.Empty(_store.GetPendingAgentEnrollments());
    }

    [Fact]
    public void TheLocalRootCannotBeReenrolled()
    {
        var rootId = _store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;

        Assert.Throws<InvalidOperationException>(() => _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, rootId));
    }

    [Fact]
    public void CodesAreReadableAndNormalisationIgnoresCaseAndDashes()
    {
        var code = AgentEnrollmentCode.Generate();

        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){4}$", code);
        Assert.DoesNotContain(code, character => "01OILU".Contains(character));
        Assert.Equal(AgentEnrollmentCode.Hash(code), AgentEnrollmentCode.Hash(" " + code.ToLowerInvariant().Replace("-", " ") + " "));
        Assert.NotEqual(AgentEnrollmentCode.Generate(), code);
        Assert.Equal(string.Empty, AgentEnrollmentCode.Hash("  - "));
    }

    [Fact]
    public void ACodeCreatesAnAgentProbeExactlyOnce()
    {
        var issue = _store.CreateAgentEnrollment("FILESRV-01", TimeSpan.FromHours(24), "admin");

        var redeemed = Redeem(_store, issue.Code, "ignored-host");

        Assert.NotNull(redeemed);
        Assert.Equal("FILESRV-01", redeemed.ProbeName);
        Assert.False(string.IsNullOrWhiteSpace(redeemed.ProbeToken));
        Assert.True(_store.TryValidateProbe(redeemed.ProbeId, redeemed.ProbeToken));
        var probe = Assert.IsType<ProbeElement>(_store.FindElement(redeemed.ElementId));
        Assert.NotNull(probe.AgentEnrolledUtc);

        Assert.Null(Redeem(_store, issue.Code, "second-machine"));
        Assert.Empty(_store.GetPendingAgentEnrollments());
    }

    [Fact]
    public void CheckingACodeForTheDownloadDoesNotConsumeIt()
    {
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);

        Assert.True(_store.IsAgentEnrollmentCodeValid(issue.Code));
        Assert.True(_store.IsAgentEnrollmentCodeValid(issue.Code.ToLowerInvariant()));
        Assert.False(_store.IsAgentEnrollmentCodeValid(AgentEnrollmentCode.Generate()));
        Assert.False(_store.IsAgentEnrollmentCodeValid(null));

        Assert.NotNull(Redeem(_store, issue.Code, "host"));
        Assert.False(_store.IsAgentEnrollmentCodeValid(issue.Code));
    }

    [Fact]
    public void WithoutANameTheProbeIsNamedAfterTheMachine()
    {
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);

        Assert.Equal("WS-17", Redeem(_store, issue.Code, "WS-17")?.ProbeName);
    }

    [Fact]
    public void WrongRevokedAndExpiredCodesAllFailTheSameWay()
    {
        _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);
        Assert.Null(Redeem(_store, AgentEnrollmentCode.Generate(), "x"));
        Assert.Null(Redeem(_store, "", "x"));

        var revoked = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);
        Assert.True(_store.RevokeAgentEnrollment(revoked.Enrollment.Id));
        Assert.Null(Redeem(_store, revoked.Code, "x"));

        // A code whose time has passed is gone, not merely refused.
        var expired = _store.CreateAgentEnrollment(null, TimeSpan.FromMilliseconds(1), null);
        Thread.Sleep(20);
        Assert.Null(Redeem(_store, expired.Code, "x"));
        Assert.DoesNotContain(_store.GetPendingAgentEnrollments(), pending => pending.Id == expired.Enrollment.Id);
    }

    [Fact]
    public void ValidityIsCappedAtAWeek()
    {
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromDays(90), null);

        Assert.True(issue.Enrollment.ExpiresUtc - issue.Enrollment.CreatedUtc <= InMemoryMonitoringWorkspaceStore.MaxAgentEnrollmentValidity);
    }

    [Fact]
    public void OnlyTheHashIsPersistedAndItSurvivesARestart()
    {
        var issue = _store.CreateAgentEnrollment("NAS", TimeSpan.FromHours(24), null);
        _store.Dispose();

        var json = File.ReadAllText(Path.Combine(_dir, "workspace.json"));
        Assert.DoesNotContain(issue.Code, json);
        Assert.DoesNotContain(AgentEnrollmentCode.Normalize(issue.Code), json);
        Assert.Contains(AgentEnrollmentCode.Hash(issue.Code), json);

        using var reopened = NewStore();
        Assert.Equal("NAS", Redeem(reopened, issue.Code, "x")?.ProbeName);
    }

    [Fact]
    public void BackupsNeverCarryPendingCodes()
    {
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(24), null);

        var package = System.Text.Encoding.UTF8.GetString(_store.CreateBackupBytes(WorkspaceBackupSection.All));

        Assert.DoesNotContain(issue.Enrollment.CodeHash, package);
    }

    public void Dispose()
    {
        _store.Dispose();
        _telemetry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

file sealed class EnrollmentHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
