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

    /// <summary>White-label product name: when set (and branding not suppressed), the partner's logo + this name
    /// replace the "Matmon" brand in the sidebar, login and page title. Null = plain co-branding (Matmon stays).</summary>
    public string? ProductName { get; set; }

    /// <summary>True when the partner logo is a complete OEM brand lockup (already includes the product name), so
    /// the brand shows the logo alone; false shows the logo with the product name stacked beneath it.</summary>
    public bool LogoIsOem { get; set; }

    // --- Co-branding (cached so it survives the cloud being offline; shown in the Cloud tab + reports) ---
    /// <summary>Partner support/contact link (distinct from a marketing website).</summary>
    public string? ContactUrl { get; set; }

    /// <summary>Brand accent colour as #RRGGBB (already validated cloud-side).</summary>
    public string? BrandColor { get; set; }

    /// <summary>Partner logo bytes (PNG or JPEG); embedded directly into the UI, the PDF and the e-mail report.</summary>
    public byte[]? LogoPng { get; set; }

    /// <summary>MIME type of <see cref="LogoPng"/> (image/png or image/jpeg) so the data: URI declares the truth.</summary>
    public string? LogoContentType { get; set; }

    // --- White-label extras (v2) ---
    /// <summary>Slogan/tagline shown beneath the product name (sidebar, login, reports). White-label only.</summary>
    public string? Slogan { get; set; }

    /// <summary>Secondary brand accent as #RRGGBB (drives hover/gradient states); null = derived from the primary.</summary>
    public string? BrandColorSecondary { get; set; }

    /// <summary>Small square logo bytes used as this instance's favicon + mobile header. White-label only.</summary>
    public byte[]? SmallLogoPng { get; set; }

    /// <summary>MIME type of <see cref="SmallLogoPng"/>.</summary>
    public string? SmallLogoContentType { get; set; }

    /// <summary>Sidebar brand layout: 0 = logo top / name below (default), 1 = logo left / name right.</summary>
    public int SidebarStyle { get; set; }

    public ServicePartnerInfo Clone() => new()
    {
        HasPartner = HasPartner,
        Name = Name,
        ContactEmail = ContactEmail,
        ContactPhone = ContactPhone,
        CanManage = CanManage,
        BrandingSuppressed = BrandingSuppressed,
        ProductName = ProductName,
        LogoIsOem = LogoIsOem,
        ContactUrl = ContactUrl,
        BrandColor = BrandColor,
        LogoPng = LogoPng is null ? null : (byte[])LogoPng.Clone(),
        LogoContentType = LogoContentType,
        Slogan = Slogan,
        BrandColorSecondary = BrandColorSecondary,
        SmallLogoPng = SmallLogoPng is null ? null : (byte[])SmallLogoPng.Clone(),
        SmallLogoContentType = SmallLogoContentType,
        SidebarStyle = SidebarStyle,
    };

    public bool ValueEquals(ServicePartnerInfo? other) =>
        other is not null
        && HasPartner == other.HasPartner
        && CanManage == other.CanManage
        && BrandingSuppressed == other.BrandingSuppressed
        && LogoIsOem == other.LogoIsOem
        && SidebarStyle == other.SidebarStyle
        && string.Equals(ProductName, other.ProductName, StringComparison.Ordinal)
        && string.Equals(Slogan, other.Slogan, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(ContactEmail, other.ContactEmail, StringComparison.Ordinal)
        && string.Equals(ContactPhone, other.ContactPhone, StringComparison.Ordinal)
        && string.Equals(ContactUrl, other.ContactUrl, StringComparison.Ordinal)
        && string.Equals(BrandColor, other.BrandColor, StringComparison.Ordinal)
        && string.Equals(BrandColorSecondary, other.BrandColorSecondary, StringComparison.Ordinal)
        && string.Equals(LogoContentType, other.LogoContentType, StringComparison.Ordinal)
        && string.Equals(SmallLogoContentType, other.SmallLogoContentType, StringComparison.Ordinal)
        && LogosEqual(LogoPng, other.LogoPng)
        && LogosEqual(SmallLogoPng, other.SmallLogoPng);

    private static bool LogosEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.AsSpan().SequenceEqual(b);
    }
}
