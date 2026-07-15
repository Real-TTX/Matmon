using Matmon.Core.Domain;

namespace Matmon.Tests;

public class BrandingSafetyTests
{
    [Theory]
    [InlineData("#AABBCC", "#AABBCC")]
    [InlineData("#aabbcc", "#AABBCC")]   // normalized to upper-case
    [InlineData("#123ABC", "#123ABC")]
    public void SafeHexColor_accepts_well_formed_hex(string input, string expected)
    {
        Assert.Equal(expected, BrandingSafety.SafeHexColor(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AABBCC")]        // missing #
    [InlineData("#AABBC")]        // too short
    [InlineData("#AABBCCD")]      // too long (8 chars)
    [InlineData("#12345678")]     // #RRGGBBAA not accepted
    [InlineData("#AABBCG")]       // non-hex digit
    [InlineData("#AA BCC")]       // space
    [InlineData("red")]
    public void SafeHexColor_rejects_malformed(string? input)
    {
        Assert.Null(BrandingSafety.SafeHexColor(input));
    }

    [Theory]
    [InlineData("https://partner.example.com/support")]
    [InlineData("http://partner.example.com")]
    public void SafeContactUrl_accepts_absolute_http_s(string input)
    {
        Assert.Equal(input, BrandingSafety.SafeContactUrl(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]   // XSS scheme
    [InlineData("data:text/html,<script>")]
    [InlineData("ftp://partner.example.com")]
    [InlineData("/relative/path")]        // not absolute
    [InlineData("partner.example.com")]   // no scheme
    public void SafeContactUrl_rejects_unsafe_or_relative(string? input)
    {
        Assert.Null(BrandingSafety.SafeContactUrl(input));
    }
}
