using Matmon.Agent;

namespace Matmon.Tests;

// The auto-update replaces the executable of a service on a customer's machine; these pin down WHEN it does.
public sealed class AgentUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADifferentVersionWithAPackageIsApplied()
    {
        var decision = AgentUpdatePolicy.Decide("0.1.41-20260901-1000", "0.1.42-20260929-1100", packageAvailable: true, null, Now);

        Assert.True(decision.ShouldUpdate);
        Assert.Equal("0.1.42-20260929-1100", decision.Version);
    }

    [Fact]
    public void TheInstanceIsFollowedDownwardsToo()
    {
        // A rolled-back instance: an agent newer than it is just as mismatched as an older one.
        Assert.True(AgentUpdatePolicy.Decide("0.1.43", "0.1.42", true, null, Now).ShouldUpdate);
    }

    [Theory]
    [InlineData("0.1.42", "0.1.42")]
    [InlineData("NIGHTLY-7", "nightly-7")]
    public void TheSameVersionIsLeftAlone(string current, string offered)
    {
        Assert.False(AgentUpdatePolicy.Decide(current, offered, true, null, Now).ShouldUpdate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local-20260929-0100")]
    public void NoOrAnUnversionedOfferIsNotAnUpdate(string? offered)
    {
        Assert.False(AgentUpdatePolicy.Decide("0.1.42", offered, true, null, Now).ShouldUpdate);
    }

    [Fact]
    public void WithoutAPackageForThisPlatformThereIsNothingToApply()
    {
        var decision = AgentUpdatePolicy.Decide("0.1.41", "0.1.42", packageAvailable: false, null, Now);

        Assert.False(decision.ShouldUpdate);
        Assert.Contains("no agent package", decision.Reason);
    }

    [Fact]
    public void ARolledBackVersionWaitsADayThenIsTriedAgain()
    {
        var rolledBack = new AgentUpdateRecord("0.1.42", "0.1.41", AgentUpdateOutcome.RolledBack, "no heartbeat", Now.AddHours(-2));

        Assert.False(AgentUpdatePolicy.Decide("0.1.41", "0.1.42", true, rolledBack, Now).ShouldUpdate);
        Assert.True(AgentUpdatePolicy.Decide("0.1.41", "0.1.42", true, rolledBack, Now.AddHours(23)).ShouldUpdate);

        // ...and a DIFFERENT release is not held back by it at all.
        Assert.True(AgentUpdatePolicy.Decide("0.1.41", "0.1.43", true, rolledBack, Now).ShouldUpdate);
    }

    [Fact]
    public void ASuccessfulUpdateDoesNotBlockTheNextOne()
    {
        var updated = new AgentUpdateRecord("0.1.42", "0.1.41", AgentUpdateOutcome.Updated, null, Now.AddMinutes(-5));

        Assert.True(AgentUpdatePolicy.Decide("0.1.42", "0.1.43", true, updated, Now).ShouldUpdate);
    }

    [Fact]
    public void TheHandshakeFilesRoundTrip()
    {
        var state = Directory.CreateTempSubdirectory("matmon-agent-update-").FullName;
        try
        {
            var files = new AgentUpdateFiles(state);
            Assert.Null(files.ReadPending());
            Assert.Null(files.ReadLastUpdate());
            Assert.False(files.IsConfirmed("0.1.42"));

            files.WritePending(new AgentUpdatePending("0.1.42", "0.1.41", Now));
            files.Confirm("0.1.42");
            files.WriteLastUpdate(new AgentUpdateRecord("0.1.42", "0.1.41", AgentUpdateOutcome.RolledBack, "no heartbeat", Now));

            Assert.Equal("0.1.42", files.ReadPending()?.Version);
            Assert.True(files.IsConfirmed("0.1.42"));
            Assert.False(files.IsConfirmed("0.1.43"));
            Assert.Equal(AgentUpdateOutcome.RolledBack, files.ReadLastUpdate()?.Outcome);
            Assert.Contains("rolled back", AgentUpdatePolicy.Describe(files.ReadLastUpdate()));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }
}
