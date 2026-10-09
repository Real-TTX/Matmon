using System.Net.WebSockets;
using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>
/// When the Full Access client connects and how it waits after a "no". It used to ask only "switched on and linked?",
/// never "does the plan include it?" - so an instance on a plan without Full Access was refused every minute for as long
/// as it ran (3705 attempts in a row on a local instance), each refusal a warning with a stack trace.
/// </summary>
public class TunnelConnectPolicyTests
{
    [Theory]
    //   Full Access on, link on, credentials, plan includes it  ->  decision
    [InlineData(false, true, true, true, TunnelDecision.Idle)]
    [InlineData(true, false, true, true, TunnelDecision.Idle)]
    [InlineData(true, true, false, true, TunnelDecision.Idle)]
    [InlineData(true, true, false, false, TunnelDecision.Idle)]        // nothing to connect: not "not included" either
    [InlineData(true, true, true, false, TunnelDecision.NotIncluded)]  // the case that hammered the cloud
    [InlineData(true, true, true, true, TunnelDecision.Connect)]
    public void TheDecisionFollowsSwitchLinkAndPlan(bool fullAccess, bool link, bool credentials, bool plan, TunnelDecision expected) =>
        Assert.Equal(expected, TunnelConnectPolicy.Decide(fullAccess, link, credentials, plan));

    [Theory]
    [InlineData("The server returned status code '403' when status code '101' was expected.", true)]
    [InlineData("The server returned status code '401' when status code '101' was expected.", true)]
    [InlineData("The server returned status code '502' when status code '101' was expected.", false)]
    [InlineData("The remote party closed the WebSocket connection without completing the close handshake.", false)]
    public void OnlyAnAnswerFromTheCloudIsADefinitiveRefusal(string message, bool definitive) =>
        Assert.Equal(definitive, TunnelConnectPolicy.IsDefinitiveRefusal(new WebSocketException(message)));

    [Fact]
    public void ANetworkErrorIsNotARefusal()
    {
        Assert.False(TunnelConnectPolicy.IsDefinitiveRefusal(new HttpRequestException("Name or service not known (matmon-cloud:8055)")));
        Assert.False(TunnelConnectPolicy.IsDefinitiveRefusal(new IOException("connection reset")));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(500, 60)] // capped, however long it keeps failing
    public void OrdinaryFailuresBackOffToAMinute(int failures, double expectedSeconds)
    {
        var withoutJitter = TunnelConnectPolicy.Backoff(failures, tickMs: 0);
        var withJitter = TunnelConnectPolicy.Backoff(failures, tickMs: 999);

        Assert.Equal(expectedSeconds, withoutJitter.TotalSeconds);
        Assert.InRange(withJitter.TotalSeconds, expectedSeconds, expectedSeconds + 1);
    }

    private static CloudConnectionSettings Settings(bool fullAccess = true, bool enabled = true, string url = "https://cloud.example", string id = "20bc758e-552e-4f3b-9c55-d54f13d603e6") =>
        new() { FullAccessEnabled = fullAccess, Enabled = enabled, Url = url, InstanceId = id, Configured = true };

    [Fact]
    public void TheSignatureChangesWithAnythingThatCouldChangeTheAnswer()
    {
        var baseline = TunnelConnectPolicy.Signature(Settings(), "token-a", planIncludesFullAccess: false);

        Assert.Equal(baseline, TunnelConnectPolicy.Signature(Settings(), "token-a", planIncludesFullAccess: false));
        Assert.NotEqual(baseline, TunnelConnectPolicy.Signature(Settings(), "token-a", planIncludesFullAccess: true));   // plan upgrade
        Assert.NotEqual(baseline, TunnelConnectPolicy.Signature(Settings(), "token-b", planIncludesFullAccess: false));  // new token after a reconnect
        Assert.NotEqual(baseline, TunnelConnectPolicy.Signature(Settings(fullAccess: false), "token-a", false));         // toggled off
        Assert.NotEqual(baseline, TunnelConnectPolicy.Signature(Settings(enabled: false), "token-a", false));            // cloud disconnected
        Assert.NotEqual(baseline, TunnelConnectPolicy.Signature(Settings(id: "00000000-0000-0000-0000-000000000002"), "token-a", false));
        Assert.NotEqual(baseline, TunnelConnectPolicy.Signature(Settings(url: "https://other.example"), "token-a", false));
    }

    [Fact]
    public void ATrailingSlashOrSpaceInTheUrlIsNotAChange() =>
        Assert.Equal(
            TunnelConnectPolicy.Signature(Settings(url: "https://cloud.example"), "t", true),
            TunnelConnectPolicy.Signature(Settings(url: " https://cloud.example/ "), "t", true));

    [Fact]
    public void TheSignatureNeverCarriesTheToken() =>
        Assert.DoesNotContain("super-secret-token", TunnelConnectPolicy.Signature(Settings(), "super-secret-token", true));

    // ---- the wait that ends early ---------------------------------------------------------------------------------

    private sealed class FakeClock
    {
        public int Waits;
        public TimeSpan Total;

        public Task Delay(TimeSpan span, CancellationToken _)
        {
            Waits++;
            Total += span;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TheWaitEndsEarlyWhenSomethingChanges()
    {
        var clock = new FakeClock();
        var signature = "before";
        // The plan is upgraded after the second look.
        string Read() => clock.Waits >= 2 ? "after" : signature;

        await TunnelConnectPolicy.WaitForChangeAsync(Read, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15), CancellationToken.None, clock.Delay);

        Assert.Equal(2, clock.Waits);                       // noticed within one poll of the change, not after ten minutes
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Total);
    }

    [Fact]
    public async Task TheWaitRunsItsFullLengthWhenNothingChanges()
    {
        var clock = new FakeClock();

        await TunnelConnectPolicy.WaitForChangeAsync(() => "same", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15), CancellationToken.None, clock.Delay);

        Assert.Equal(40, clock.Waits);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.Total);
    }

    [Fact]
    public async Task TheLastStepIsShortenedSoTheWaitNeverOvershoots()
    {
        var clock = new FakeClock();

        await TunnelConnectPolicy.WaitForChangeAsync(() => "same", TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(15), CancellationToken.None, clock.Delay);

        Assert.Equal(4, clock.Waits);                        // 15 + 15 + 15 + 5
        Assert.Equal(TimeSpan.FromSeconds(50), clock.Total);
    }

    [Fact]
    public async Task ShuttingDownEndsTheWaitAtOnce()
    {
        var clock = new FakeClock();
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        await TunnelConnectPolicy.WaitForChangeAsync(() => "same", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15), shutdown.Token, clock.Delay);

        Assert.Equal(0, clock.Waits);
    }

    // ---- what the Config page says ---------------------------------------------------------------------------------

    [Fact]
    public void TheTunnelStateSaysWhenThePlanDoesNotIncludeFullAccess()
    {
        var state = new TunnelState();
        state.SetEnabled(true);
        state.MarkDisconnected("old refusal", failure: true);

        state.SetNotIncluded(true);
        var notIncluded = state.Snapshot();
        state.SetNotIncluded(false);

        Assert.True(notIncluded.NotIncludedInPlan);
        Assert.False(notIncluded.Connected);
        Assert.Null(notIncluded.LastError);              // the stale refusal is no longer the story
        Assert.Equal(0, notIncluded.ConsecutiveFailures); // so an upgrade connects without a long backoff
        Assert.False(state.Snapshot().NotIncludedInPlan);
    }
}
