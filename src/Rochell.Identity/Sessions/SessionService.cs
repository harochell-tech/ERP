using System.Data.Common;
using System.Net;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Ids;
using Rochell.Platform.Time;

namespace Rochell.Identity.Sessions;

/// <summary>Claims of an ID token already validated by the OIDC middleware (signature, issuer, audience, expiry).</summary>
public sealed record OidcClaims(string Subject, string? Email, bool EmailVerified, string? HostedDomain);

/// <summary>A role the session's user holds now in a company, company-wide (<see cref="PlantId"/> null) or for one plant.</summary>
public sealed record SessionAssignment(string RoleCode, string RoleName, Guid? PlantId);

/// <summary>What the user may do in one company: assignments valid now and the permissions they grant.</summary>
public sealed record SessionCompany(Guid CompanyId, string LegalName, IReadOnlyList<SessionAssignment> Assignments, IReadOnlyList<string> Permissions);

/// <summary>An open session as the UI needs it: who, until when, whether a step-up is still fresh, and where the user can act.</summary>
public sealed record SessionDescription(
    Guid SessionId,
    Guid UserId,
    string? Email,
    DateTime LoginAt,
    DateTime? LastStepUpAt,
    DateTime? StepUpValidUntil,
    DateTime ExpiresAt,
    IReadOnlyList<SessionCompany> Companies);

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

    /// <summary>
    /// Describes a usable session (same rules as authorization, E-PR03-6) without touching its activity. Throws
    /// <see cref="DomainException"/> with <see cref="AuthorizationErrors.SessionInvalid"/> or <see cref="AuthorizationErrors.SessionExpired"/>.
    /// Assignments are read company by company because they are protected by row-level security.
    /// </summary>
    public async Task<SessionDescription> DescribeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var session = await Reading.SingleOrDefaultAsync(
            connection,
            null,
            """
            SELECT s.user_id, u.email, s.login_at, s.last_activity_at, s.last_step_up_at, s.logout_at, u.status, u.kind
            FROM iam.session s
            JOIN iam.user u ON u.user_id = s.user_id
            WHERE s.session_id = @session_id
            """,
            r => new SessionRow(r.GetGuid(0), r.NullableString(1), r.Utc(2), r.Utc(3), r.NullableUtc(4), r.NullableUtc(5), r.GetString(6), r.GetString(7)),
            cancellationToken,
            ("session_id", sessionId)).ConfigureAwait(false)
            ?? throw new DomainException(AuthorizationErrors.SessionInvalid, "The session does not exist.");
        SessionRules.EnsureUsable(_options, now, session.LoginAt, session.LastActivityAt, session.LogoutAt, session.Status, session.Kind);

        var companies = await Reading.ListAsync(
            connection,
            null,
            "SELECT company_id, legal_name FROM md.company ORDER BY legal_name, company_id",
            r => (Id: r.GetGuid(0), Name: r.GetString(1)),
            cancellationToken).ConfigureAwait(false);

        var result = new List<SessionCompany>();
        foreach (var (companyId, legalName) in companies)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @c, true)", cancellationToken, ("c", companyId.ToString())).ConfigureAwait(false);
            var assignments = await Reading.ListAsync(
                connection,
                transaction,
                """
                SELECT r.code, r.name, ra.plant_id, array_agg(rp.permission_code ORDER BY rp.permission_code)
                FROM iam.role_assignment ra
                JOIN iam.role r ON r.role_id = ra.role_id
                JOIN iam.role_permission rp ON rp.role_id = ra.role_id
                WHERE ra.company_id = @c AND ra.user_id = @u AND ra.valid_from <= @now AND (ra.valid_to IS NULL OR ra.valid_to > @now)
                GROUP BY r.code, r.name, ra.plant_id
                ORDER BY r.code, ra.plant_id NULLS FIRST
                """,
                r => (Assignment: new SessionAssignment(r.GetString(0), r.GetString(1), r.NullableGuid(2)), Permissions: r.GetFieldValue<string[]>(3)),
                cancellationToken,
                ("c", companyId),
                ("u", session.UserId),
                ("now", now)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (assignments.Count > 0)
            {
                var permissions = assignments.SelectMany(a => a.Permissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                result.Add(new SessionCompany(companyId, legalName, assignments.Select(a => a.Assignment).ToList(), permissions));
            }
        }

        var stepUpValidUntil = session.LastStepUpAt + _options.StepUpMaxAge;
        return new SessionDescription(
            sessionId,
            session.UserId,
            session.Email,
            session.LoginAt,
            session.LastStepUpAt,
            stepUpValidUntil > now ? stepUpValidUntil : null,
            SessionRules.ExpiresAt(_options, session.LoginAt, session.LastActivityAt),
            result);
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

    private sealed record SessionRow(Guid UserId, string? Email, DateTime LoginAt, DateTime LastActivityAt, DateTime? LastStepUpAt, DateTime? LogoutAt, string Status, string Kind);

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
