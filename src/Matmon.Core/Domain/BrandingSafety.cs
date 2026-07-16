namespace Matmon.Core.Domain;

/// <summary>
/// Render-time re-validation of cloud-supplied co-branding values. The cloud sanitizes on write, but the
/// instance persists whatever it fetched verbatim, so every consumer (the app accent override, the Cloud tab,
/// the PDF/e-mail reports) passes the value through here before it reaches inline CSS or an href. Pure, so it
/// is the single tested guard against a malicious/garbage cloud value becoming a CSS or javascript: injection.
/// </summary>
public static class BrandingSafety
{
    /// <summary>The colour only when it is a well-formed <c>#RRGGBB</c> hex string (normalized upper-case), else null.</summary>
    public static string? SafeHexColor(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#')
        {
            return null;
        }

        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return null;
            }
        }

        return value.ToUpperInvariant();
    }

    /// <summary>The URL only when it is a safe absolute http/https link, else null (blocks <c>javascript:</c> etc.).</summary>
    public static string? SafeContactUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;

    /// <summary>The content type derived from the image's MAGIC BYTES ("image/png"/"image/jpeg"), or null for
    /// anything else. Used to validate cloud-supplied logo bytes at CACHE time: the instance serves the cached
    /// logo same-origin and anonymously (/api/branding/logo|favicon), so a script-bearing SVG with a spoofed
    /// content type from a compromised cloud must never be stored - and the served MIME comes from here, never
    /// from the payload.</summary>
    public static string? DetectRasterContentType(byte[]? bytes)
    {
        if (bytes is { Length: >= 8 }
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        return bytes is { Length: >= 3 } && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
            ? "image/jpeg"
            : null;
    }
}
