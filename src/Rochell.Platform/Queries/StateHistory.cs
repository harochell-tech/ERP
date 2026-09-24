using Rochell.Platform.Data;

namespace Rochell.Platform.Queries;

/// <summary>One status change of a document (ADR-027): when, by whom (e-mail of the session's user) and through which command.</summary>
public sealed record StateChange(string StatusKind, string? From, string To, string Command, string? Reason, DateTime At, string? By);

public static class StateHistory
{
    /// <summary>The document's status changes in the order they were recorded.</summary>
    public static Task<List<StateChange>> ReadAsync(QueryContext context, string aggregateType, Guid aggregateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT h.status_kind, h.from_state, h.to_state, h.command, h.reason, e.recorded_at, u.email
            FROM core.state_history h
            JOIN core.domain_event e ON e.event_id = h.event_id
            LEFT JOIN iam.session s ON s.session_id = e.session_id
            LEFT JOIN iam.user u ON u.user_id = s.user_id
            WHERE h.company_id = @c AND h.aggregate_type = @t AND h.aggregate_id = @id
            ORDER BY e.recorded_at, e.command_event_index, h.state_history_id
            """,
            r => new StateChange(r.GetString(0), r.NullableString(1), r.GetString(2), r.GetString(3), r.NullableString(4), r.Utc(5), r.NullableString(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", aggregateType),
            ("id", aggregateId));
    }
}
