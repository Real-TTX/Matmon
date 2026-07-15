namespace Matmon.Core.Domain;

/// <summary>The instance's managing service partner, fetched from Matmon.Cloud on heartbeat and cached.
/// <see cref="HasPartner"/> is false when no partner manages the instance's organization.
/// <see cref="CanManage"/> is the customer's consent for that partner to access this instance.</summary>
public sealed class ServicePartnerInfo
{
    public bool HasPartner { get; set; }

    public string? Name { get; set; }

    public string? ContactEmail { get; set; }

    public string? ContactPhone { get; set; }

    /// <summary>Customer consent: whether the managing partner may access/operate this instance.</summary>
    public bool CanManage { get; set; }

    /// <summary>Customer opt-out of visual co-branding: when true the logo/accent/"Managed by" are suppressed on
    /// the UI + reports, but the relationship (<see cref="HasPartner"/>) + consent tab stay so consent is revocable.</summary>
    public bool BrandingSuppressed { get; set; }

    // --- Co-branding (cached so it survives the cloud being offline; shown in the Cloud tab + reports) ---
    /// <summary>Partner support/contact link (distinct from a marketing website).</summary>
    public string? ContactUrl { get; set; }

    /// <summary>Brand accent colour as #RRGGBB (already validated cloud-side).</summary>
    public string? BrandColor { get; set; }

    /// <summary>Partner logo bytes (PNG or JPEG); embedded directly into the UI, the PDF and the e-mail report.</summary>
    public byte[]? LogoPng { get; set; }

    /// <summary>MIME type of <see cref="LogoPng"/> (image/png or image/jpeg) so the data: URI declares the truth.</summary>
    public string? LogoContentType { get; set; }

    public ServicePartnerInfo Clone() => new()
    {
        HasPartner = HasPartner,
        Name = Name,
        ContactEmail = ContactEmail,
        ContactPhone = ContactPhone,
        CanManage = CanManage,
        BrandingSuppressed = BrandingSuppressed,
        ContactUrl = ContactUrl,
        BrandColor = BrandColor,
        LogoPng = LogoPng is null ? null : (byte[])LogoPng.Clone(),
        LogoContentType = LogoContentType,
    };

    public bool ValueEquals(ServicePartnerInfo? other) =>
        other is not null
        && HasPartner == other.HasPartner
        && CanManage == other.CanManage
        && BrandingSuppressed == other.BrandingSuppressed
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(ContactEmail, other.ContactEmail, StringComparison.Ordinal)
        && string.Equals(ContactPhone, other.ContactPhone, StringComparison.Ordinal)
        && string.Equals(ContactUrl, other.ContactUrl, StringComparison.Ordinal)
        && string.Equals(BrandColor, other.BrandColor, StringComparison.Ordinal)
        && string.Equals(LogoContentType, other.LogoContentType, StringComparison.Ordinal)
        && LogosEqual(LogoPng, other.LogoPng);

    private static bool LogosEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.AsSpan().SequenceEqual(b);
    }
}
