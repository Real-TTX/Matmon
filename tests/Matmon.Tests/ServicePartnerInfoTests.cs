using Matmon.Core.Domain;

namespace Matmon.Tests;

public class ServicePartnerInfoTests
{
    private static ServicePartnerInfo Sample() => new()
    {
        HasPartner = true,
        Name = "ACME MSP",
        ContactEmail = "help@acme.example",
        ContactPhone = "+49 111",
        CanManage = true,
        ContactUrl = "https://acme.example/support",
        BrandColor = "#AABBCC",
        LogoPng = [1, 2, 3, 4],
        LogoContentType = "image/png",
    };

    [Fact]
    public void Clone_deep_copies_logo_bytes()
    {
        var original = Sample();
        var clone = original.Clone();

        clone.LogoPng![0] = 99;

        // The clone's array is independent - mutating it must not touch the original (CLAUDE.md: a shared
        // reference here would let a later mutation corrupt the cached document).
        Assert.Equal(1, original.LogoPng![0]);
        Assert.NotSame(original.LogoPng, clone.LogoPng);
    }

    [Fact]
    public void Clone_produces_a_value_equal_copy()
    {
        var original = Sample();
        Assert.True(original.ValueEquals(original.Clone()));
    }

    [Fact]
    public void ValueEquals_false_for_null_other()
    {
        Assert.False(Sample().ValueEquals(null));
    }

    [Fact]
    public void ValueEquals_detects_a_logo_byte_difference()
    {
        var a = Sample();
        var b = Sample();
        b.LogoPng = [1, 2, 3, 5];   // last byte differs

        Assert.False(a.ValueEquals(b));
    }

    [Theory]
    [InlineData("brand")]
    [InlineData("url")]
    [InlineData("name")]
    [InlineData("consent")]
    public void ValueEquals_detects_scalar_field_changes(string field)
    {
        var a = Sample();
        var b = Sample();
        switch (field)
        {
            case "brand": b.BrandColor = "#000000"; break;
            case "url": b.ContactUrl = "https://other.example"; break;
            case "name": b.Name = "Other"; break;
            case "consent": b.CanManage = false; break;
        }

        Assert.False(a.ValueEquals(b));
    }

    [Fact]
    public void ValueEquals_treats_both_null_logos_as_equal()
    {
        var a = Sample();
        var b = Sample();
        a.LogoPng = null;
        b.LogoPng = null;

        Assert.True(a.ValueEquals(b));
    }

    [Fact]
    public void ValueEquals_false_when_only_one_logo_is_null()
    {
        var a = Sample();
        var b = Sample();
        b.LogoPng = null;

        Assert.False(a.ValueEquals(b));
    }
}
