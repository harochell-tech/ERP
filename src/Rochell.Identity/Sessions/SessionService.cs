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
/// A role the session's user holds now in a company, company-wide (<see cref="PlantId"/> null) or for one plant, with the
/// permissions it grants (the UI derives each permission's plant scope from it, E-PR18b-8).
/// </summary>
public sealed record SessionAssignment(string RoleCode, string RoleName, Guid? PlantId, IReadOnlyList<string> Permissions);

/// <summary>What the user may do in one company: assignments valid now and the permissions they grant.</summary>
public sealed record SessionCompany(Guid CompanyId, string LegalName, IReadOnlyList<SessionAssignment> Assignments, IReadOnlyList<string> Permissions);

/// <summary>
/// An open session as the UI needs it: who, until when, whether a step-up is still fresh, and where the user can act.
/// <see cref="AuthenticatedEmail"/> is set only on an acting session (E-B03-14): the person signed in behind the test identity.
/// </summary>
public sealed record SessionDescription(
    Guid SessionId,
    Guid UserId,
    string? Email,
    DateTime LoginAt,
    DateTime? LastStepUpAt,
    DateTime? StepUpValidUntil,
    DateTime ExpiresAt,
    IReadOnlyList<SessionCompany> Companies,
    string? AuthenticatedEmail = null);

/// <summary>A synthetic user a tester may act as in one company (E-B03-14), with the roles it holds there.</summary>
public sealed record TestIdentity(Guid UserId, string Email, IReadOnlyList<string> Roles);

/// <summary>
/// Office sessions (ADR-012, ADR-038). Login and re-authentication accept only verified Google Workspace identities
/// of the configured domain whose subject belongs to an active human user with an employee (E-PR03-8).
/// MFA is enforced by Google Workspace, not verified in the token.
/// </summary>
public sealed class SessionService
{
    public const string LoginRejected = "LOGIN_REJECTED";

    /// <summary>The synthetic user does not exist, is disabled or holds no role in that company (E-B03-14).</summary>
    public const string TestIdentityUnavailable = "TEST_IDENTITY_UNAVAILABLE";

    public const string ActAsPermission = "identity:act_as";

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

    /// <summary>
    /// E-FIS1-04-7: opens a SERVICE session for an active service identity — only the API's daily process calls this; the session
    /// never reaches a cookie and <see cref="DescribeAsync"/> rejects it. The caller ends it with <see cref="EndSessionAsync"/>.
    /// </summary>
    public async Task<Guid> StartServiceSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var user = Sql.Command(connection, null, "SELECT kind = 'SERVICE' AND status = 'ACTIVE' FROM iam.user WHERE user_id = @id", ("id", userId)))
        {
            if (await user.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new DomainException(AuthorizationErrors.SessionInvalid, "Not an active service identity.");
            }
        }

        var sessionId = _ids.NewId();
        await Sql.ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO iam.session (session_id, user_id, auth_method, login_at, last_activity_at)
            VALUES (@session_id, @user_id, @auth_method, @now, @now)
            """,
            cancellationToken,
            ("session_id", sessionId),
            ("user_id", userId),
            ("auth_method", IdentityConstants.AuthMethodService),
            ("now", _clock.UtcNow)).ConfigureAwait(false);
        return sessionId;
    }

    /// <summary>
    /// Records a re-authentication (step-up) by the same Google identity that owns the session — for an acting session, the
    /// person signed in behind it (E-B03-14).
    /// </summary>
    public async Task RecordStepUpAsync(Guid sessionId, OidcClaims claims, CancellationToken cancellationToken = default)
    {
        ValidateClaims(claims);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var userId = await FindActiveHumanAsync(connection, claims.Subject, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var updated = await Sql.ExecuteAsync(
            connection,
            null,
            """
            UPDATE iam.session SET last_step_up_at = @now, last_activity_at = @now
            WHERE session_id = @session_id AND logout_at IS NULL
              AND (user_id = @user_id
                OR authenticated_session_id IN (SELECT session_id FROM iam.session WHERE user_id = @user_id AND logout_at IS NULL))
            """,
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
            SELECT s.user_id, u.email, s.login_at, s.last_activity_at, s.last_step_up_at, s.logout_at, u.status, u.kind,
                   p.session_id, p.user_id, pu.email, p.login_at, p.logout_at, pu.status, pu.kind
            FROM iam.session s
            JOIN iam.user u ON u.user_id = s.user_id
            LEFT JOIN iam.session p ON p.session_id = s.authenticated_session_id
            LEFT JOIN iam.user pu ON pu.user_id = p.user_id
            WHERE s.session_id = @session_id
            """,
            ReadSessionRow,
            cancellationToken,
            ("session_id", sessionId)).ConfigureAwait(false)
            ?? throw new DomainException(AuthorizationErrors.SessionInvalid, "The session does not exist.");
        SessionRules.EnsureUsable(_options, now, session.LoginAt, session.LastActivityAt, session.LogoutAt, session.Status, session.Kind, session.Parent?.Rules);

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
                r => new SessionAssignment(r.GetString(0), r.GetString(1), r.NullableGuid(2), r.GetFieldValue<string[]>(3)),
                cancellationToken,
                ("c", companyId),
                ("u", session.UserId),
                ("now", now)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (assignments.Count > 0)
            {
                var permissions = assignments.SelectMany(a => a.Permissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                result.Add(new SessionCompany(companyId, legalName, assignments, permissions));
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
            result,
            session.Parent?.Email);
    }

    /// <summary>
    /// The synthetic users the person behind <paramref name="sessionId"/> may act as in <paramref name="companyId"/> (E-B03-14).
    /// Needs identity:act_as there, which only TEST databases can grant.
    /// </summary>
    public async Task<IReadOnlyList<TestIdentity>> ListTestIdentitiesAsync(Guid sessionId, Guid companyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var person = await ReadPersonAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireActAsAsync(connection, transaction, companyId, person.UserId, cancellationToken).ConfigureAwait(false);
        var identities = await ReadIdentitiesAsync(connection, transaction, companyId, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return identities;
    }

    /// <summary>
    /// Opens an acting session as <paramref name="syntheticUserId"/> for the person behind <paramref name="sessionId"/>, closing the
    /// acting session it came from, if any (E-B03-14). The new session starts without a step-up: actions that require one
    /// re-authenticate the person with Google. Returns the new session id (the cookie's new value).
    /// </summary>
    public async Task<Guid> ActAsAsync(Guid sessionId, Guid companyId, Guid syntheticUserId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var person = await ReadPersonAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireActAsAsync(connection, transaction, companyId, person.UserId, cancellationToken).ConfigureAwait(false);
        if ((await ReadIdentitiesAsync(connection, transaction, companyId, syntheticUserId, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            throw new DomainException(TestIdentityUnavailable, "That test identity does not exist or has no role in this company.");
        }

        var now = _clock.UtcNow;
        var actingId = _ids.NewId();
        await Sql.ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO iam.session (session_id, user_id, auth_method, login_at, last_activity_at, authenticated_session_id)
            VALUES (@session_id, @user_id, 'ACT_AS', @now, @now, @authenticated)
            """,
            cancellationToken,
            ("session_id", actingId),
            ("user_id", syntheticUserId),
            ("now", now),
            ("authenticated", person.SessionId)).ConfigureAwait(false);
        if (sessionId != person.SessionId)
        {
            await CloseAsync(connection, transaction, sessionId, now, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return actingId;
    }

    /// <summary>Ends an acting session and returns the signed-in session behind it (a signed-in session is returned as is).</summary>
    public async Task<Guid> StopActingAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var person = await ReadPersonAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionId != person.SessionId)
        {
            await CloseAsync(connection, null, sessionId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        return person.SessionId;
    }

    public async Task EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            connection,
            null,
            """
            UPDATE iam.session SET logout_at = @now
            WHERE logout_at IS NULL
              AND (session_id = @session_id
                OR session_id = (SELECT authenticated_session_id FROM iam.session WHERE session_id = @session_id)
                OR authenticated_session_id = @session_id
                OR authenticated_session_id = (SELECT authenticated_session_id FROM iam.session WHERE session_id = @session_id))
            """,
            cancellationToken,
            ("now", _clock.UtcNow),
            ("session_id", sessionId)).ConfigureAwait(false);
    }

    private static SessionRow ReadSessionRow(DbDataReader r)
        => new(
            r.GetGuid(0), r.NullableString(1), r.Utc(2), r.Utc(3), r.NullableUtc(4), r.NullableUtc(5), r.GetString(6), r.GetString(7),
            r.IsDBNull(8)
                ? null
                : new ParentRow(r.GetGuid(8), r.GetGuid(9), r.NullableString(10), new ParentSession(r.Utc(11), r.NullableUtc(12), r.GetString(13), r.GetString(14))));

    /// <summary>The usable signed-in session and person behind <paramref name="sessionId"/> (itself unless it is an acting session).</summary>
    private async Task<(Guid SessionId, Guid UserId)> ReadPersonAsync(DbConnection connection, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await Reading.SingleOrDefaultAsync(
            connection,
            null,
            """
            SELECT s.user_id, u.email, s.login_at, s.last_activity_at, s.last_step_up_at, s.logout_at, u.status, u.kind,
                   p.session_id, p.user_id, pu.email, p.login_at, p.logout_at, pu.status, pu.kind
            FROM iam.session s
            JOIN iam.user u ON u.user_id = s.user_id
            LEFT JOIN iam.session p ON p.session_id = s.authenticated_session_id
            LEFT JOIN iam.user pu ON pu.user_id = p.user_id
            WHERE s.session_id = @session_id
            """,
            ReadSessionRow,
            cancellationToken,
            ("session_id", sessionId)).ConfigureAwait(false)
            ?? throw new DomainException(AuthorizationErrors.SessionInvalid, "The session does not exist.");
        SessionRules.EnsureUsable(_options, _clock.UtcNow, session.LoginAt, session.LastActivityAt, session.LogoutAt, session.Status, session.Kind, session.Parent?.Rules);
        return session.Parent is { } parent ? (parent.SessionId, parent.UserId) : (sessionId, session.UserId);
    }

    private async Task RequireActAsAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid personId, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @c, true)", cancellationToken, ("c", companyId.ToString())).ConfigureAwait(false);
        await using var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT EXISTS (
              SELECT 1 FROM iam.role_assignment ra JOIN iam.role_permission rp ON rp.role_id = ra.role_id
              WHERE ra.company_id = @c AND ra.user_id = @u AND rp.permission_code = @permission
                AND ra.valid_from <= @now AND (ra.valid_to IS NULL OR ra.valid_to > @now))
            """,
            ("c", companyId),
            ("u", personId),
            ("permission", ActAsPermission),
            ("now", _clock.UtcNow));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new DomainException(AuthorizationErrors.NotAuthorized, $"Acting as a test identity needs {ActAsPermission} in this company.");
        }
    }

    private async Task<IReadOnlyList<TestIdentity>> ReadIdentitiesAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid? only, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT u.user_id, u.email, array_agg(DISTINCT r.code ORDER BY r.code)
            FROM iam.user u
            JOIN iam.role_assignment ra ON ra.user_id = u.user_id
            JOIN iam.role r ON r.role_id = ra.role_id
            WHERE u.kind = 'SYNTHETIC' AND u.status = 'ACTIVE' AND ra.company_id = @c
              AND ra.valid_from <= @now AND (ra.valid_to IS NULL OR ra.valid_to > @now)
              AND (CAST(@only AS uuid) IS NULL OR u.user_id = CAST(@only AS uuid))
            GROUP BY u.user_id, u.email
            ORDER BY u.email
            """,
            r => new TestIdentity(r.GetGuid(0), r.GetString(1), r.GetFieldValue<string[]>(2)),
            cancellationToken,
            ("c", companyId),
            ("now", _clock.UtcNow),
            ("only", (object?)only ?? DBNull.Value)).ConfigureAwait(false);

    private static Task CloseAsync(DbConnection connection, DbTransaction? transaction, Guid sessionId, DateTime now, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            connection,
            transaction,
            "UPDATE iam.session SET logout_at = @now WHERE session_id = @session_id AND logout_at IS NULL",
            cancellationToken,
            ("now", now),
            ("session_id", sessionId));

    private sealed record ParentRow(Guid SessionId, Guid UserId, string? Email, ParentSession Rules);

    private sealed record SessionRow(Guid UserId, string? Email, DateTime LoginAt, DateTime LastActivityAt, DateTime? LastStepUpAt, DateTime? LogoutAt, string Status, string Kind, ParentRow? Parent);

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
