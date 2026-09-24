using System.Data.Common;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Identity.Authorization;

/// <summary>
/// Authorization inside the command transaction: open session of an active human user, not expired
/// (absolute and idle, E-PR03-6), a role assignment valid now in the command's company (and plant, for plant-scoped
/// commands) granting the permission, and a recent re-authentication when the handler requires it (E-PR03-2).
/// Commands that are not plant-scoped require a company-wide assignment (plant_id IS NULL).
/// </summary>
public sealed class SqlCommandAuthorizer : ICommandAuthorizer
{
    private readonly IdentityOptions _options;
    private readonly IClock _clock;

    public SqlCommandAuthorizer(IdentityOptions options, IClock? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? SystemClock.Instance;
    }

    public async Task AuthorizeAsync(
        DbConnection connection,
        DbTransaction transaction,
        ICommand command,
        RequiresPermissionAttribute requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(requirement);
        var now = _clock.UtcNow;
        var session = await ReadSessionAsync(connection, transaction, command.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(AuthorizationErrors.SessionInvalid, "The session does not exist.");

        if (session.LogoutAt is not null || session.Status != "ACTIVE" || session.Kind != "HUMAN")
        {
            throw new DomainException(AuthorizationErrors.SessionInvalid, "The session is closed or its user is not active.");
        }

        if (now - session.LoginAt >= _options.SessionAbsoluteLifetime || now - session.LastActivityAt >= _options.SessionIdleTimeout)
        {
            throw new DomainException(AuthorizationErrors.SessionExpired, "The session has expired; sign in again.");
        }

        Guid? plantId = command is IPlantScopedCommand scoped ? scoped.PlantId : null;
        await using (var permission = Sql.Command(
            connection,
            transaction,
            """
            SELECT EXISTS (
              SELECT 1
              FROM iam.role_assignment ra
              JOIN iam.role_permission rp ON rp.role_id = ra.role_id
              WHERE ra.company_id = @company_id
                AND ra.user_id = @user_id
                AND rp.permission_code = @permission
                AND ra.valid_from <= @now
                AND (ra.valid_to IS NULL OR ra.valid_to > @now)
                AND ((CAST(@plant_id AS uuid) IS NULL AND ra.plant_id IS NULL)
                  OR (CAST(@plant_id AS uuid) IS NOT NULL AND (ra.plant_id IS NULL OR ra.plant_id = CAST(@plant_id AS uuid)))))
            """,
            ("company_id", command.CompanyId),
            ("user_id", session.UserId),
            ("permission", requirement.Permission),
            ("now", now),
            ("plant_id", plantId)))
        {
            if (await permission.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new DomainException(AuthorizationErrors.NotAuthorized, $"The user does not have permission '{requirement.Permission}' for this company/plant.");
            }
        }

        if (requirement.StepUp && (session.LastStepUpAt is null || now - session.LastStepUpAt.Value > _options.StepUpMaxAge))
        {
            throw new DomainException(AuthorizationErrors.StepUpRequired, "This action requires re-authentication.");
        }

        await Sql.ExecuteAsync(
            connection,
            transaction,
            "UPDATE iam.session SET last_activity_at = @now WHERE session_id = @session_id AND last_activity_at < @threshold",
            cancellationToken,
            ("now", now),
            ("session_id", command.SessionId),
            ("threshold", now - _options.ActivityTouchInterval)).ConfigureAwait(false);
    }

    private static async Task<SessionRow?> ReadSessionAsync(DbConnection connection, DbTransaction transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT s.user_id, s.login_at, s.last_activity_at, s.last_step_up_at, s.logout_at, u.status, u.kind
            FROM iam.session s
            JOIN iam.user u ON u.user_id = s.user_id
            WHERE s.session_id = @session_id
            """,
            ("session_id", sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SessionRow(
            reader.GetGuid(0),
            reader.GetFieldValue<DateTime>(1),
            reader.GetFieldValue<DateTime>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTime>(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    private sealed record SessionRow(Guid UserId, DateTime LoginAt, DateTime LastActivityAt, DateTime? LastStepUpAt, DateTime? LogoutAt, string Status, string Kind);
}
