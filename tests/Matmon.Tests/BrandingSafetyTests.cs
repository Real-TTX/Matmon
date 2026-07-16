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

    [Fact]
    public void DetectRasterContentType_recognizes_png_and_jpeg_magic_bytes()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0];

        Assert.Equal("image/png", BrandingSafety.DetectRasterContentType(png));
        Assert.Equal("image/jpeg", BrandingSafety.DetectRasterContentType(jpeg));
    }

    [Fact]
    public void DetectRasterContentType_rejects_svg_and_garbage()
    {
        // A script-bearing SVG spoofed as image/png by a compromised cloud must NOT be cached/served -
        // the instance serves the cached logo same-origin and anonymously (stored-XSS vector).
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        Assert.Null(BrandingSafety.DetectRasterContentType(svg));
        Assert.Null(BrandingSafety.DetectRasterContentType(null));
        Assert.Null(BrandingSafety.DetectRasterContentType([]));
        Assert.Null(BrandingSafety.DetectRasterContentType([0x89, 0x50])); // truncated PNG header
    }
}
