using System.Data.Common;
using System.Net;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Ids;
using Rochell.Platform.Time;

namespace Rochell.Identity.Sessions;

/// <summary>Claims of an ID token already validated by the OIDC middleware (signature, issuer, audience, expiry).</summary>
public sealed record OidcClaims(string Subject, string? Email, bool EmailVerified, string? HostedDomain);

/// <summary>
/// Office sessions (ADR-012, ADR-038). Login and re-authentication accept only verified Google Workspace identities
/// of the configured domain whose subject belongs to an active human user with an employee (E-PR03-8).
/// MFA is enforced by Google Workspace, not verified in the token.
/// </summary>
public sealed class SessionService
{
    public const string LoginRejected = "LOGIN_REJECTED";

    private readonly DbDataSource _dataSource;
    private readonly IdentityOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public SessionService(DbDataSource dataSource, IdentityOptions options, IClock? clock = null, IIdGenerator? ids = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? SystemClock.Instance;
        _ids = ids ?? UuidV7Generator.Instance;
    }

    /// <summary>Creates a session. A fresh login also counts as a re-authentication.</summary>
    public async Task<Guid> StartOidcSessionAsync(OidcClaims claims, IPAddress? ip, string? userAgent, CancellationToken cancellationToken = default)
    {
        ValidateClaims(claims);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var userId = await FindActiveHumanAsync(connection, claims.Subject, cancellationToken).ConfigureAwait(false);

        var sessionId = _ids.NewId();
        var now = _clock.UtcNow;
        await Sql.ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO iam.session (session_id, user_id, auth_method, ip, user_agent, login_at, last_step_up_at, last_activity_at)
            VALUES (@session_id, @user_id, @auth_method, @ip, @user_agent, @now, @now, @now)
            """,
            cancellationToken,
            ("session_id", sessionId),
            ("user_id", userId),
            ("auth_method", IdentityConstants.AuthMethodOidcGoogle),
            ("ip", ip),
            ("user_agent", userAgent),
            ("now", now)).ConfigureAwait(false);
        return sessionId;
    }

    /// <summary>Records a re-authentication (step-up) by the same Google identity that owns the session.</summary>
    public async Task RecordStepUpAsync(Guid sessionId, OidcClaims claims, CancellationToken cancellationToken = default)
    {
        ValidateClaims(claims);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var userId = await FindActiveHumanAsync(connection, claims.Subject, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var updated = await Sql.ExecuteAsync(
            connection,
            null,
            "UPDATE iam.session SET last_step_up_at = @now, last_activity_at = @now WHERE session_id = @session_id AND user_id = @user_id AND logout_at IS NULL",
            cancellationToken,
            ("now", now),
            ("session_id", sessionId),
            ("user_id", userId)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new DomainException(LoginRejected, "Re-authentication does not match an open session of this user.");
        }
    }

    public async Task EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            connection,
            null,
            "UPDATE iam.session SET logout_at = @now WHERE session_id = @session_id AND logout_at IS NULL",
            cancellationToken,
            ("now", _clock.UtcNow),
            ("session_id", sessionId)).ConfigureAwait(false);
    }

    private void ValidateClaims(OidcClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (string.IsNullOrWhiteSpace(claims.Subject))
        {
            throw new DomainException(LoginRejected, "The identity token has no subject.");
        }

        if (!claims.EmailVerified)
        {
            throw new DomainException(LoginRejected, "The e-mail of the Google account is not verified.");
        }

        if (!string.Equals(claims.HostedDomain, _options.HostedDomain, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(LoginRejected, "The Google account does not belong to the organization's domain.");
        }
    }

    private static async Task<Guid> FindActiveHumanAsync(DbConnection connection, string subject, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            null,
            "SELECT user_id FROM iam.user WHERE oidc_subject = @subject AND kind = 'HUMAN' AND status = 'ACTIVE' AND employee_id IS NOT NULL",
            ("subject", subject));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid userId
            ? userId
            : throw new DomainException(LoginRejected, "No active user is linked to this Google account.");
    }
}
