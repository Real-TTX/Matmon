using Matmon.Host.Pages;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Tests;

/// <summary>What System > Updates says after the update screen comes back.</summary>
public sealed class UpdateScreenResultTests
{
    private static SelfUpdateStatus Status(string current, SelfUpdateOutcome? outcome) =>
        new(SelfUpdateMode.Docker, null, current, "release", null, null, null, outcome, false, null);

    [Fact]
    public void A_fresh_record_of_this_update_decides()
    {
        var updated = SystemUpdatesModel.DescribeResult("1.0", "1.1",
            Status("1.1", new SelfUpdateOutcome(SelfUpdateOutcomeKind.Updated, "1.0", "1.1", DateTimeOffset.UtcNow, null)));
        Assert.Equal("success", updated.Tone);
        Assert.Contains("updated to 1.1", updated.Message);

        var rolledBack = SystemUpdatesModel.DescribeResult("1.0", "1.1",
            Status("1.0", new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, "1.0", "1.1", DateTimeOffset.UtcNow, "exited at once")));
        Assert.Equal("danger", rolledBack.Tone);
        Assert.Contains("rolled back", rolledBack.Message);
        Assert.Equal("exited at once", rolledBack.Detail);
    }

    [Fact]
    public void Without_a_record_yet_the_running_version_tells()
    {
        // A Docker helper writes its record a moment after the new container is up.
        Assert.Equal("success", SystemUpdatesModel.DescribeResult("1.0", "1.1", Status("1.1", null)).Tone);

        var unchanged = SystemUpdatesModel.DescribeResult("1.0", "1.1", Status("1.0", null));
        Assert.Equal("warning", unchanged.Tone);
        Assert.Contains("did not complete", unchanged.Message);
    }

    [Fact]
    public void An_old_record_or_one_of_another_update_does_not_count()
    {
        var old = new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, "1.0", "1.1", DateTimeOffset.UtcNow.AddDays(-2), null);
        Assert.Equal("success", SystemUpdatesModel.DescribeResult("1.0", "1.2", Status("1.2", old)).Tone);

        var other = new SelfUpdateOutcome(SelfUpdateOutcomeKind.Failed, "0.9", "1.0", DateTimeOffset.UtcNow, null);
        Assert.Equal("warning", SystemUpdatesModel.DescribeResult("1.0", "1.1", Status("1.0", other)).Tone);
    }
}
