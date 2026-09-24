using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Reconciliation;

internal static class ReopenSql
{
    public static async Task<(Guid PeriodId, string Component, Guid RequestedBy)> RequestedAsync(CommandContext context, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT period_id, component, requested_by, status FROM fin.reopen_request WHERE company_id = @c AND request_id = @r FOR UPDATE",
            ("c", context.CompanyId),
            ("r", requestId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(3) != "REQUESTED")
        {
            throw new DomainException(ReconciliationErrors.ReopenNotRequested, $"Reopen request {requestId} is not pending.");
        }

        return (reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2));
    }
}

/// <summary>Patch 1 P-8: CLOSED stays CLOSED; a pending request waits for a second approver.</summary>
[RequiresPermission("period_component:reopen", StepUp = true)]
public sealed class RequestReopenHandler : ICommandHandler<RequestReopen>
{
    public string CommandType => "Finance.RequestReopen";

    public async Task<string> HandleAsync(RequestReopen command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        CloseSql.RequireComponent(command.Component);
        var reason = CloseSql.RequireReason(command.Reason);
        var (status, _) = await CloseSql.ComponentStateAsync(context, command.PeriodId, command.Component, cancellationToken).ConfigureAwait(false);
        if (status != "CLOSED")
        {
            throw new DomainException(ReconciliationErrors.ComponentNotClosed, $"{command.Component} of this period is {status}; only a CLOSED component is reopened.");
        }

        await using (var pending = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT count(*) FROM fin.reopen_request WHERE company_id = @c AND period_id = @p AND component = @k AND status = 'REQUESTED'",
            ("c", context.CompanyId),
            ("p", command.PeriodId),
            ("k", command.Component)))
        {
            if ((long)(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0)
            {
                throw new DomainException(ReconciliationErrors.ReopenAlreadyRequested, "A reopen request for this component is already pending.");
            }
        }

        var requestId = context.ResultRef;
        var user = await CloseSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("ReopenRequested", 1, "ReopenRequest", requestId, 1, JsonSerializer.Serialize(new { periodId = command.PeriodId, component = command.Component, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.reopen_request (request_id, company_id, period_id, component, reason, requested_by, requested_at, status)
            VALUES (@r, @c, @p, @k, @reason, @u, @t, 'REQUESTED')
            """,
            cancellationToken,
            ("r", requestId),
            ("c", context.CompanyId),
            ("p", command.PeriodId),
            ("k", command.Component),
            ("reason", reason),
            ("u", user),
            ("t", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendStateAsync("ReopenRequest", requestId, "DOCUMENT", null, "REQUESTED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { requestId });
    }
}

/// <summary>T-14: the second approver (≠ requester) reopens: the component goes CLOSED → REOPENED under the period lock.</summary>
[RequiresPermission("period_component:second_approve", StepUp = true)]
public sealed class ApproveReopenHandler : ICommandHandler<ApproveReopen>
{
    public string CommandType => "Finance.ApproveReopen";

    public async Task<string> HandleAsync(ApproveReopen command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (periodId, component, requestedBy) = await ReopenSql.RequestedAsync(context, command.RequestId, cancellationToken).ConfigureAwait(false);
        var approver = await CloseSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == requestedBy)
        {
            throw new DomainException(ReconciliationErrors.SamePerson, "The person who asked to reopen cannot approve it.");
        }

        await CloseSql.LockPeriodExclusiveAsync(context, periodId, component, cancellationToken).ConfigureAwait(false);
        var (status, version) = await CloseSql.ComponentStateAsync(context, periodId, component, cancellationToken).ConfigureAwait(false);
        if (status != "CLOSED")
        {
            throw new DomainException(ReconciliationErrors.ComponentNotClosed, $"{component} of this period is {status}.");
        }

        var now = context.Clock.UtcNow;
        var eventId = await context.AppendEventAsync(
            new EventDraft("ComponentReopened", 1, "ReopenRequest", command.RequestId, 2, JsonSerializer.Serialize(new { periodId, component }), Publish: true, OccurredAt: now),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.close_component_state SET status = 'REOPENED', version = version + 1 WHERE company_id = @c AND period_id = @p AND component = @k",
            cancellationToken,
            ("c", context.CompanyId),
            ("p", periodId),
            ("k", component)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.reopen_request SET status = 'APPROVED', second_approved_by = @u, second_approved_at = @t WHERE company_id = @c AND request_id = @r",
            cancellationToken,
            ("u", approver),
            ("t", now),
            ("c", context.CompanyId),
            ("r", command.RequestId)).ConfigureAwait(false);
        await context.AppendStateAsync("ReopenRequest", command.RequestId, "DOCUMENT", "REQUESTED", "APPROVED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync($"PeriodComponent:{component}", periodId, "DOCUMENT", "CLOSED", "REOPENED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { periodId, component, version = version + 1 });
    }
}

[RequiresPermission("period_component:second_approve", StepUp = true)]
public sealed class RejectReopenHandler : ICommandHandler<RejectReopen>
{
    public string CommandType => "Finance.RejectReopen";

    public async Task<string> HandleAsync(RejectReopen command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = CloseSql.RequireReason(command.Reason);
        var (periodId, component, requestedBy) = await ReopenSql.RequestedAsync(context, command.RequestId, cancellationToken).ConfigureAwait(false);
        var rejecter = await CloseSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (rejecter == requestedBy)
        {
            throw new DomainException(ReconciliationErrors.SamePerson, "The person who asked to reopen cannot decide on it.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("ReopenRejected", 1, "ReopenRequest", command.RequestId, 2, JsonSerializer.Serialize(new { periodId, component, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.reopen_request SET status = 'REJECTED', rejected_by = @u WHERE company_id = @c AND request_id = @r",
            cancellationToken,
            ("u", rejecter),
            ("c", context.CompanyId),
            ("r", command.RequestId)).ConfigureAwait(false);
        await context.AppendStateAsync("ReopenRequest", command.RequestId, "DOCUMENT", "REQUESTED", "REJECTED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { requestId = command.RequestId });
    }
}
