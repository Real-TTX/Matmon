using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services;

/// <summary>
/// The short-lived, DataProtection-encrypted cookie that carries "password verified, awaiting the 2nd factor".
/// Not an auth cookie - it only names the pending user id (+ the post-login returnUrl) for the /login/2fa step and
/// expires after a few minutes. Encrypted + HttpOnly so the browser can't read or forge it.
/// </summary>
public sealed class Pending2faCookie
{
    public const string Name = "matmon_2fa";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector;

    public Pending2faCookie(IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector("Matmon.Instance.Pending2fa.v1");
    }

    public void Issue(HttpContext http, Guid userId, string? returnUrl)
    {
        var payload = $"{userId:N}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{returnUrl}";
        http.Response.Cookies.Append(Name, _protector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = Ttl
        });
    }

    public (Guid UserId, string? ReturnUrl)? Read(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(Name, out var raw) || string.IsNullOrEmpty(raw))
        {
            return null;
        }
        try
        {
            var parts = _protector.Unprotect(raw).Split('|', 3);
            var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1]));
            if (DateTimeOffset.UtcNow - issued > Ttl)
            {
                return null;
            }
            var returnUrl = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : null;
            return (Guid.ParseExact(parts[0], "N"), returnUrl);
        }
        catch
        {
            return null;
        }
    }

    public void Clear(HttpContext http) => http.Response.Cookies.Delete(Name);
}
