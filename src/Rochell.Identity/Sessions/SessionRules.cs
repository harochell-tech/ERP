using Rochell.Platform.Commands;

namespace Rochell.Identity.Sessions;

/// <summary>The signed-in Google session behind an acting session (E-B03-14).</summary>
internal sealed record ParentSession(DateTime LoginAt, DateTime? LogoutAt, string UserStatus, string UserKind);

/// <summary>
/// When a session can still act (E-PR03-6): open, of an active human user, within the absolute lifetime and the idle timeout.
/// An acting session (E-B03-14) is of a synthetic user and lives only while the signed-in session behind it is open, of an
/// active person, and within that session's absolute lifetime.
/// </summary>
internal static class SessionRules
{
    public static void EnsureUsable(IdentityOptions options, DateTime now, DateTime loginAt, DateTime lastActivityAt, DateTime? logoutAt, string userStatus, string userKind, ParentSession? parent = null)
    {
        var kindAllowed = parent is null ? userKind == "HUMAN" : userKind == "SYNTHETIC";
        if (logoutAt is not null || userStatus != "ACTIVE" || !kindAllowed)
        {
            throw new DomainException(AuthorizationErrors.SessionInvalid, "The session is closed or its user is not active.");
        }

        if (parent is not null && (parent.LogoutAt is not null || parent.UserStatus != "ACTIVE" || parent.UserKind != "HUMAN"))
        {
            throw new DomainException(AuthorizationErrors.SessionInvalid, "The signed-in session behind this identity has ended.");
        }

        if (now - loginAt >= options.SessionAbsoluteLifetime || now - lastActivityAt >= options.SessionIdleTimeout
            || (parent is not null && now - parent.LoginAt >= options.SessionAbsoluteLifetime))
        {
            throw new DomainException(AuthorizationErrors.SessionExpired, "The session has expired; sign in again.");
        }
    }

    /// <summary>The instant the session expires if nothing else happens.</summary>
    public static DateTime ExpiresAt(IdentityOptions options, DateTime loginAt, DateTime lastActivityAt)
    {
        var absolute = loginAt + options.SessionAbsoluteLifetime;
        var idle = lastActivityAt + options.SessionIdleTimeout;
        return absolute < idle ? absolute : idle;
    }
}
