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
}
