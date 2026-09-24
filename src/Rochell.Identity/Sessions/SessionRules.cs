using Rochell.Platform.Commands;

namespace Rochell.Identity.Sessions;

/// <summary>When a session can still act (E-PR03-6): open, of an active human user, within the absolute lifetime and the idle timeout.</summary>
internal static class SessionRules
{
    public static void EnsureUsable(IdentityOptions options, DateTime now, DateTime loginAt, DateTime lastActivityAt, DateTime? logoutAt, string userStatus, string userKind)
    {
        if (logoutAt is not null || userStatus != "ACTIVE" || userKind != "HUMAN")
        {
            throw new DomainException(AuthorizationErrors.SessionInvalid, "The session is closed or its user is not active.");
        }

        if (now - loginAt >= options.SessionAbsoluteLifetime || now - lastActivityAt >= options.SessionIdleTimeout)
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
