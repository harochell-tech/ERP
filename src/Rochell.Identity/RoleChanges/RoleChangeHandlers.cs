using System.Data.Common;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Identity.RoleChanges;

internal static class RoleChangeSql
{
    public const string RequestAggregate = "RoleAssignmentRequest";
    public const string AssignmentAggregate = "RoleAssignment";

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @id", ("id", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static async Task<Guid> RoleIdAsync(CommandContext context, string roleCode, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT role_id FROM iam.role WHERE code = @code", ("code", roleCode));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid roleId
            ? roleId
            : throw new DomainException(RoleChangeErrors.RoleUnknown, $"Role '{roleCode}' does not exist.");
    }

    public static async Task EnsureActiveHumanAsync(CommandContext context, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM iam.user WHERE user_id = @id AND kind = 'HUMAN' AND status = 'ACTIVE')",
            ("id", userId));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new DomainException(RoleChangeErrors.UserInvalid, "The target user does not exist or is not an active human user.");
        }
    }

    public static async Task<Guid?> ActiveAssignmentAsync(CommandContext context, Guid userId, Guid roleId, Guid? plantId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT assignment_id FROM iam.role_assignment
            WHERE company_id = @company_id AND user_id = @user_id AND role_id = @role_id AND valid_to IS NULL
              AND plant_id IS NOT DISTINCT FROM CAST(@plant_id AS uuid)
            """ + (forUpdate ? " FOR UPDATE" : string.Empty),
            ("company_id", context.CompanyId),
            ("user_id", userId),
            ("role_id", roleId),
            ("plant_id", plantId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    public static async Task<string> CreateRequestAsync(
        CommandContext context,
        string commandType,
        string action,
        Guid targetUserId,
        string roleCode,
        Guid? plantId,
        CancellationToken cancellationToken)
    {
        var requester = await SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (requester == targetUserId)
        {
            throw new DomainException(RoleChangeErrors.SelfRequest, "A user cannot request role changes for themselves.");
        }

        await EnsureActiveHumanAsync(context, targetUserId, cancellationToken).ConfigureAwait(false);
        var roleId = await RoleIdAsync(context, roleCode, cancellationToken).ConfigureAwait(false);
        var active = await ActiveAssignmentAsync(context, targetUserId, roleId, plantId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        if (action == "REVOKE" && active is null)
        {
            throw new DomainException(RoleChangeErrors.AssignmentNotFound, "The user has no active assignment of that role.");
        }

        if (action == "ASSIGN" && active is not null)
        {
            throw new DomainException(RoleChangeErrors.AlreadyAssigned, "The user already has that role.");
        }

        var payload = JsonSerializer.Serialize(new { requestId = context.ResultRef, action, userId = targetUserId, roleCode, plantId, requestedBy = requester });
        var eventId = await context.AppendEventAsync(
            new EventDraft("RoleChangeRequested", 1, RequestAggregate, context.ResultRef, 1, payload, Publish: true),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO iam.role_assignment_request (company_id, request_id, user_id, role_id, plant_id, action, requested_by, status)
            VALUES (@company_id, @request_id, @user_id, @role_id, @plant_id, @action, @requested_by, 'REQUESTED')
            """,
            cancellationToken,
            ("company_id", context.CompanyId),
            ("request_id", context.ResultRef),
            ("user_id", targetUserId),
            ("role_id", roleId),
            ("plant_id", plantId),
            ("action", action),
            ("requested_by", requester)).ConfigureAwait(false);

        await context.AppendStateAsync(RequestAggregate, context.ResultRef, "DOCUMENT", null, "REQUESTED", commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { requestId = context.ResultRef, status = "REQUESTED" });
    }
}

[RequiresPermission("role:assign", StepUp = true)]
public sealed class RequestRoleAssignmentHandler : ICommandHandler<RequestRoleAssignment>
{
    public string CommandType => "Identity.RequestRoleAssignment";

    public Task<string> HandleAsync(RequestRoleAssignment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return RoleChangeSql.CreateRequestAsync(context, CommandType, "ASSIGN", command.TargetUserId, command.RoleCode, command.PlantId, cancellationToken);
    }
}

[RequiresPermission("role:revoke", StepUp = true)]
public sealed class RequestRoleRevocationHandler : ICommandHandler<RequestRoleRevocation>
{
    public string CommandType => "Identity.RequestRoleRevocation";

    public Task<string> HandleAsync(RequestRoleRevocation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return RoleChangeSql.CreateRequestAsync(context, CommandType, "REVOKE", command.TargetUserId, command.RoleCode, command.PlantId, cancellationToken);
    }
}

[RequiresPermission("role:second_approve", StepUp = true)]
public sealed class ApproveRoleChangeHandler : ICommandHandler<ApproveRoleChange>
{
    public string CommandType => "Identity.ApproveRoleChange";

    public async Task<string> HandleAsync(ApproveRoleChange command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var approver = await RoleChangeSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var request = await ReadRequestAsync(context, command.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(RoleChangeErrors.RequestNotFound, "The role change request does not exist.");

        if (request.Status != "REQUESTED")
        {
            throw new DomainException(RoleChangeErrors.RequestNotPending, $"The request is already {request.Status}.");
        }

        if (approver == request.RequestedBy || approver == request.UserId)
        {
            throw new DomainException(RoleChangeErrors.SecondApproverRequired, "The approver must be different from the requester and from the affected user.");
        }

        var approvedPayload = JsonSerializer.Serialize(new { requestId = command.RequestId, approvedBy = approver });
        var approvedEvent = await context.AppendEventAsync(
            new EventDraft("RoleChangeApproved", 1, RoleChangeSql.RequestAggregate, command.RequestId, 2, approvedPayload, Publish: true),
            cancellationToken).ConfigureAwait(false);

        Guid assignmentId;
        if (request.Action == "ASSIGN")
        {
            assignmentId = context.Ids.NewId();
            var assignedEvent = await context.AppendEventAsync(
                new EventDraft(
                    "RoleAssigned",
                    1,
                    RoleChangeSql.AssignmentAggregate,
                    assignmentId,
                    1,
                    JsonSerializer.Serialize(new { assignmentId, requestId = command.RequestId, userId = request.UserId, roleId = request.RoleId, plantId = request.PlantId }),
                    Publish: true,
                    CausationId: approvedEvent),
                cancellationToken).ConfigureAwait(false);
            await InsertAssignmentAsync(context, assignmentId, request, cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(RoleChangeSql.AssignmentAggregate, assignmentId, "DOCUMENT", null, "ACTIVE", CommandType, assignedEvent, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            assignmentId = await RoleChangeSql.ActiveAssignmentAsync(context, request.UserId, request.RoleId, request.PlantId, forUpdate: true, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException(RoleChangeErrors.AssignmentNotFound, "The assignment to revoke is no longer active.");
            var revokedEvent = await context.AppendEventAsync(
                new EventDraft(
                    "RoleRevoked",
                    1,
                    RoleChangeSql.AssignmentAggregate,
                    assignmentId,
                    2,
                    JsonSerializer.Serialize(new { assignmentId, requestId = command.RequestId }),
                    Publish: true,
                    CausationId: approvedEvent),
                cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE iam.role_assignment SET valid_to = @now WHERE assignment_id = @id",
                cancellationToken,
                ("now", context.Clock.UtcNow),
                ("id", assignmentId)).ConfigureAwait(false);
            await context.AppendStateAsync(RoleChangeSql.AssignmentAggregate, assignmentId, "DOCUMENT", "ACTIVE", "REVOKED", CommandType, revokedEvent, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE iam.role_assignment_request SET status = 'APPROVED', second_approved_by = @approver WHERE request_id = @id",
            cancellationToken,
            ("approver", approver),
            ("id", command.RequestId)).ConfigureAwait(false);
        await context.AppendStateAsync(RoleChangeSql.RequestAggregate, command.RequestId, "DOCUMENT", "REQUESTED", "APPROVED", CommandType, approvedEvent, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { requestId = command.RequestId, status = "APPROVED", assignmentId });
    }

    private static async Task InsertAssignmentAsync(CommandContext context, Guid assignmentId, RequestRow request, CancellationToken cancellationToken)
    {
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
                VALUES (@id, @company_id, @user_id, @role_id, @plant_id, @valid_from, @granted_by)
                """,
                cancellationToken,
                ("id", assignmentId),
                ("company_id", context.CompanyId),
                ("user_id", request.UserId),
                ("role_id", request.RoleId),
                ("plant_id", request.PlantId),
                ("valid_from", context.Clock.UtcNow),
                ("granted_by", request.RequestedBy)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.RaiseException && ex.Message.Contains("SOD_CONFLICT", StringComparison.Ordinal))
        {
            throw new DomainException(RoleChangeErrors.SodConflict, ex.Message);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(RoleChangeErrors.AlreadyAssigned, "The user already has that role.");
        }
    }

    private static async Task<RequestRow?> ReadRequestAsync(CommandContext context, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT user_id, role_id, plant_id, action, requested_by, status
            FROM iam.role_assignment_request
            WHERE company_id = @company_id AND request_id = @request_id
            FOR UPDATE
            """,
            ("company_id", context.CompanyId),
            ("request_id", requestId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RequestRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetGuid(4),
            reader.GetString(5));
    }

    private sealed record RequestRow(Guid UserId, Guid RoleId, Guid? PlantId, string Action, Guid RequestedBy, string Status);
}
