using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Identity.Invitations;

// SEC-02 (E-SEC-02-8): the invitations of the company with their state, and the requests to disable or enable a user.

public sealed record ListInvitations(Guid CompanyId, Guid SessionId, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record InvitationRoleView(string RoleCode, string RoleName, Guid? PlantId, string? PlantCode);

/// <remarks><c>State</c> is the status as the screen shows it: an APPROVED invitation past its date reads EXPIRED.</remarks>
public sealed record InvitationView(
    Guid InvitationId, string Email, string DisplayName, bool Guest, DateTime? RolesValidTo, string Status, string State, Guid RequestedById, string? RequestedBy,
    DateTime RequestedAt, string? ApprovedBy, DateTime? ApprovedAt, DateTime? ExpiresAt, string? DecidedBy, DateTime? DecidedAt, string? DecisionReason,
    DateTime? AcceptedAt, IReadOnlyList<InvitationRoleView> Roles);

public sealed record InvitationList(IReadOnlyList<InvitationView> Items, int Limit, int Offset);

[RequiresPermission("iam:read")]
public sealed class ListInvitationsHandler : IQueryHandler<ListInvitations>
{
    public string QueryType => "Identity.ListInvitations";

    public async Task<string> HandleAsync(ListInvitations query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var now = context.Clock.UtcNow;
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.invitation_id, i.email, i.display_name, i.guest, i.roles_valid_to, i.status,
                   CASE WHEN i.status = 'APPROVED' AND i.expires_at <= @now THEN 'EXPIRED' ELSE i.status END,
                   i.requested_by, coalesce(rq.display_name, rq.email), i.requested_at, coalesce(ap.display_name, ap.email), i.approved_at, i.expires_at,
                   coalesce(dc.display_name, dc.email), i.decided_at, i.decision_reason, i.accepted_at
            FROM iam.invitation i
            JOIN iam.user rq ON rq.user_id = i.requested_by
            LEFT JOIN iam.user ap ON ap.user_id = i.second_approved_by
            LEFT JOIN iam.user dc ON dc.user_id = coalesce(i.rejected_by, i.cancelled_by)
            WHERE i.company_id = @c AND (CAST(@s AS text) IS NULL OR i.status = CAST(@s AS text))
            ORDER BY i.requested_at DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new InvitationView(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.NullableUtc(4), r.GetString(5), r.GetString(6), r.GetGuid(7), r.NullableString(8), r.Utc(9),
                r.NullableString(10), r.NullableUtc(11), r.NullableUtc(12), r.NullableString(13), r.NullableUtc(14), r.NullableString(15), r.NullableUtc(16), []),
            cancellationToken,
            ("c", context.CompanyId),
            ("now", now),
            ("s", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        var ids = items.Select(i => i.InvitationId).ToArray();
        var roles = ids.Length == 0
            ? []
            : await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT ir.invitation_id, r.code, r.name, ir.plant_id, p.code
                FROM iam.invitation_role ir JOIN iam.role r ON r.role_id = ir.role_id LEFT JOIN md.plant p ON p.plant_id = ir.plant_id
                WHERE ir.invitation_id = ANY(@ids) ORDER BY r.code
                """,
                r => (Id: r.GetGuid(0), View: new InvitationRoleView(r.GetString(1), r.GetString(2), r.NullableGuid(3), r.NullableString(4))),
                cancellationToken,
                ("ids", ids)).ConfigureAwait(false);
        var byId = roles.ToLookup(x => x.Id, x => x.View);
        return ApiJson.Serialize(new InvitationList(items.Select(i => i with { Roles = byId[i.InvitationId].ToList() }).ToList(), query.Limit, query.Offset));
    }
}

public sealed record ListUserStatusRequests(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record UserStatusRequestView(
    Guid RequestId, Guid UserId, string? UserEmail, string? UserName, string Action, string Reason, string Status, Guid RequestedById, string? RequestedBy, DateTime RequestedAt,
    string? DecidedBy, DateTime? DecidedAt);

public sealed record UserStatusRequestList(IReadOnlyList<UserStatusRequestView> Items);

[RequiresPermission("iam:read")]
public sealed class ListUserStatusRequestsHandler : IQueryHandler<ListUserStatusRequests>
{
    public string QueryType => "Identity.ListUserStatusRequests";

    public async Task<string> HandleAsync(ListUserStatusRequests query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT q.request_id, q.user_id, u.email, u.display_name, q.action, q.reason, q.status, q.requested_by, coalesce(rq.display_name, rq.email), q.requested_at,
                   coalesce(dc.display_name, dc.email), q.decided_at
            FROM iam.user_status_request q
            JOIN iam.user u ON u.user_id = q.user_id
            JOIN iam.user rq ON rq.user_id = q.requested_by
            LEFT JOIN iam.user dc ON dc.user_id = coalesce(q.second_approved_by, q.rejected_by)
            WHERE q.company_id = @c AND (CAST(@s AS text) IS NULL OR q.status = CAST(@s AS text))
            ORDER BY q.requested_at DESC
            LIMIT 200
            """,
            r => new UserStatusRequestView(
                r.GetGuid(0), r.GetGuid(1), r.NullableString(2), r.NullableString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetGuid(7), r.NullableString(8), r.Utc(9),
                r.NullableString(10), r.NullableUtc(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new UserStatusRequestList(items));
    }
}
