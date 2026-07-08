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

    public ServicePartnerInfo Clone() => new()
    {
        HasPartner = HasPartner,
        Name = Name,
        ContactEmail = ContactEmail,
        ContactPhone = ContactPhone,
        CanManage = CanManage,
    };

    public bool ValueEquals(ServicePartnerInfo? other) =>
        other is not null
        && HasPartner == other.HasPartner
        && CanManage == other.CanManage
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(ContactEmail, other.ContactEmail, StringComparison.Ordinal)
        && string.Equals(ContactPhone, other.ContactPhone, StringComparison.Ordinal);
}
