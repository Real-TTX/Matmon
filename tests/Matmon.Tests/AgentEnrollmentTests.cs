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

        var redeemed = _store.RedeemAgentEnrollment(issue.Code, "ignored-host");

        Assert.NotNull(redeemed);
        Assert.Equal("FILESRV-01", redeemed.ProbeName);
        Assert.False(string.IsNullOrWhiteSpace(redeemed.ProbeToken));
        Assert.True(_store.TryValidateProbe(redeemed.ProbeId, redeemed.ProbeToken));
        var probe = Assert.IsType<ProbeElement>(_store.FindElement(redeemed.ElementId));
        Assert.NotNull(probe.AgentEnrolledUtc);

        Assert.Null(_store.RedeemAgentEnrollment(issue.Code, "second-machine"));
        Assert.Empty(_store.GetPendingAgentEnrollments());
    }

    [Fact]
    public void WithoutANameTheProbeIsNamedAfterTheMachine()
    {
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);

        Assert.Equal("WS-17", _store.RedeemAgentEnrollment(issue.Code, "WS-17")?.ProbeName);
    }

    [Fact]
    public void WrongRevokedAndExpiredCodesAllFailTheSameWay()
    {
        _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);
        Assert.Null(_store.RedeemAgentEnrollment(AgentEnrollmentCode.Generate(), "x"));
        Assert.Null(_store.RedeemAgentEnrollment("", "x"));

        var revoked = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null);
        Assert.True(_store.RevokeAgentEnrollment(revoked.Enrollment.Id));
        Assert.Null(_store.RedeemAgentEnrollment(revoked.Code, "x"));

        // A code whose time has passed is gone, not merely refused.
        var expired = _store.CreateAgentEnrollment(null, TimeSpan.FromMilliseconds(1), null);
        Thread.Sleep(20);
        Assert.Null(_store.RedeemAgentEnrollment(expired.Code, "x"));
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
        Assert.Equal("NAS", reopened.RedeemAgentEnrollment(issue.Code, "x")?.ProbeName);
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
