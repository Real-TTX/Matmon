using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// The plans (2026-10-22): the free tier is "Personal" - personal use only, unlimited sensors, one probe. The tier on the
/// wire stays Free (0), so tokens and the cloud keep working; only what a user reads changed.
/// </summary>
public sealed class LicensePlanTests
{
    [Fact]
    public void Without_a_license_the_instance_is_on_the_free_Personal_plan_with_unlimited_sensors_and_one_probe()
    {
        var fallback = LicenseInfo.Fallback();

        Assert.Equal(LicenseTier.Free, fallback.Tier);
        Assert.Equal("Personal", fallback.DisplayName);
        Assert.True(fallback.IsSensorUnlimited);
        Assert.Equal(1, fallback.ProbeLimit);
        Assert.False(fallback.TunnelEnabled);
    }

    [Fact]
    public void A_named_plan_keeps_its_name_and_a_legacy_token_shows_its_tier()
    {
        Assert.Equal("Business-2026", new LicenseInfo { Tier = LicenseTier.Business, PlanName = "Business-2026" }.DisplayName);
        Assert.Equal("Enterprise", new LicenseInfo { Tier = LicenseTier.Enterprise }.DisplayName);
        Assert.Equal("Personal", new LicenseInfo { Tier = LicenseTier.Free }.DisplayName);
    }
}
