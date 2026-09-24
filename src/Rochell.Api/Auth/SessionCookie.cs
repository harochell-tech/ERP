using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Rochell.Api.Auth;

/// <summary>
/// E-PR18-2: the iam.session id travels in an HttpOnly, Secure, SameSite=Strict host-only cookie. The value is protected
/// (encrypted and authenticated) with ASP.NET Data Protection, so a session id read from the database (command_log,
/// domain_event) cannot be turned into a cookie. The database session stays the authority: expiry, logout and step-up are
/// evaluated there on every request.
/// </summary>
public sealed class SessionCookie(IDataProtectionProvider protection)
{
    public const string Name = "__Host-rochell-session";
    private readonly IDataProtector _protector = protection.CreateProtector("Rochell.Api.SessionCookie.v1");

    public Guid? Read(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Cookies.TryGetValue(Name, out var value) ? Unprotect(value) : null;
    }

    public void Write(HttpContext http, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(Name, Protect(sessionId), Options());
    }

    public void Delete(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(Name, Options());
    }

    /// <summary>The session id as carried inside the OIDC state during a step-up (the callback is cross-site: the Strict cookie is absent).</summary>
    public string Protect(Guid sessionId) => _protector.Protect(sessionId.ToString("N"));

    public Guid? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Guid.TryParseExact(_protector.Unprotect(value), "N", out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
