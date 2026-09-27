using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Identity.Queries;

// E-UI01-4: users, their roles and the role change requests, read with iam:read (security roles, Auditor, Director). Users are
// created with the deployment CLI (E-UI01-5 (a)); the screens assign and revoke roles through requests with a second approver.

public sealed record ListUsers(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record UserRoleView(Guid AssignmentId, string RoleCode, string RoleName, Guid? PlantId, string? PlantCode, DateTime ValidFrom);

/// <summary>A user with an assignment or a request in the company, or one with no assignment anywhere yet (just created).</summary>
public sealed record UserView(Guid UserId, string? Email, string Kind, string Status, IReadOnlyList<UserRoleView> Roles);

public sealed record UserList(IReadOnlyList<UserView> Items);

[RequiresPermission("iam:read")]
public sealed class ListUsersHandler : IQueryHandler<ListUsers>
{
    public string QueryType => "Identity.ListUsers";

    private sealed record Row(Guid UserId, string? Email, string Kind, string Status, Guid? AssignmentId, string? RoleCode, string? RoleName, Guid? PlantId, string? PlantCode, DateTime? ValidFrom);

    public async Task<string> HandleAsync(ListUsers query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT u.user_id, u.email, u.kind, u.status, ra.assignment_id, r.code, r.name, ra.plant_id, p.code, ra.valid_from
            FROM iam.user u
            LEFT JOIN iam.role_assignment ra ON ra.user_id = u.user_id AND ra.company_id = @c AND ra.valid_from <= @now AND (ra.valid_to IS NULL OR ra.valid_to > @now)
            LEFT JOIN iam.role r ON r.role_id = ra.role_id
            LEFT JOIN md.plant p ON p.plant_id = ra.plant_id
            WHERE u.kind <> 'SERVICE'
              AND (EXISTS (SELECT 1 FROM iam.role_assignment x WHERE x.user_id = u.user_id AND x.company_id = @c)
                OR EXISTS (SELECT 1 FROM iam.role_assignment_request q WHERE q.user_id = u.user_id AND q.company_id = @c)
                OR NOT EXISTS (SELECT 1 FROM iam.role_assignment y WHERE y.user_id = u.user_id))
            ORDER BY u.kind, u.email, r.code
            """,
            r => new Row(r.GetGuid(0), r.NullableString(1), r.GetString(2), r.GetString(3), r.NullableGuid(4), r.NullableString(5), r.NullableString(6), r.NullableGuid(7), r.NullableString(8), r.NullableUtc(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        var users = rows.GroupBy(r => (r.UserId, r.Email, r.Kind, r.Status))
            .Select(g => new UserView(
                g.Key.UserId, g.Key.Email, g.Key.Kind, g.Key.Status,
                g.Where(r => r.AssignmentId is not null).Select(r => new UserRoleView(r.AssignmentId!.Value, r.RoleCode!, r.RoleName!, r.PlantId, r.PlantCode, r.ValidFrom!.Value)).ToList()))
            .ToList();
        return ApiJson.Serialize(new UserList(users));
    }
}

public sealed record ListRoleRequests(Guid CompanyId, Guid SessionId, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record RoleRequestView(
    Guid RequestId, Guid UserId, string? UserEmail, string Action, string RoleCode, string RoleName, Guid? PlantId, string? PlantCode, string Status,
    Guid RequestedById, string? RequestedBy, DateTime? RequestedAt, string? ApprovedBy, string? RejectedBy, DateTime? RejectedAt, string? RejectionReason);

public sealed record RoleRequestList(IReadOnlyList<RoleRequestView> Items, int Limit, int Offset);

[RequiresPermission("iam:read")]
public sealed class ListRoleRequestsHandler : IQueryHandler<ListRoleRequests>
{
    public string QueryType => "Identity.ListRoleRequests";

    public async Task<string> HandleAsync(ListRoleRequests query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT q.request_id, q.user_id, u.email, q.action, r.code, r.name, q.plant_id, p.code, q.status,
                   q.requested_by, rb.email,
                   (SELECT min(e.recorded_at) FROM core.domain_event e WHERE e.company_id = q.company_id AND e.aggregate_id = q.request_id AND e.event_type = 'RoleChangeRequested'),
                   ab.email, xb.email, q.rejected_at, q.rejection_reason
            FROM iam.role_assignment_request q
            JOIN iam.user u ON u.user_id = q.user_id
            JOIN iam.role r ON r.role_id = q.role_id
            JOIN iam.user rb ON rb.user_id = q.requested_by
            LEFT JOIN md.plant p ON p.plant_id = q.plant_id
            LEFT JOIN iam.user ab ON ab.user_id = q.second_approved_by
            LEFT JOIN iam.user xb ON xb.user_id = q.rejected_by
            WHERE q.company_id = @c AND (CAST(@status AS text) IS NULL OR q.status = CAST(@status AS text))
            ORDER BY q.status = 'REQUESTED' DESC, 12 DESC NULLS LAST, q.request_id
            LIMIT @limit OFFSET @offset
            """,
            r => new RoleRequestView(
                r.GetGuid(0), r.GetGuid(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.NullableGuid(6), r.NullableString(7), r.GetString(8),
                r.GetGuid(9), r.NullableString(10), r.NullableUtc(11), r.NullableString(12), r.NullableString(13), r.NullableUtc(14), r.NullableString(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new RoleRequestList(items, query.Limit, query.Offset));
    }
}
