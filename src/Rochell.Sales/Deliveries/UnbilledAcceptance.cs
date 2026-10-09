using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Deliveries;

/// <summary>
/// X1-01b (E-X1-3, E-X1-01-6, E-X1-01b-1/2): the Controller accepts, with a reason, the month's deliveries not invoiced, so the month can
/// close (CONTRACT-ASSET's UNBILLED_AT_CLOSE). The acceptance covers the lines unbilled at that moment; one that appears later holds the
/// close again.
/// </summary>
public sealed record AcceptUnbilledDeliveries(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PeriodId, string Reason) : ICommand;

[RequiresPermission("unbilled_delivery:accept", StepUp = true)]
public sealed class AcceptUnbilledDeliveriesHandler : ICommandHandler<AcceptUnbilledDeliveries>
{
    public const string NothingToAccept = "NOTHING_TO_ACCEPT";

    public string CommandType => "Sales.AcceptUnbilledDeliveries";

    public async Task<string> HandleAsync(AcceptUnbilledDeliveries command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = SalesSql.Optional(command.Reason, 500, "The reason");
        if (reason is null || reason.Length < 3)
        {
            throw new DomainException(SalesErrors.FieldRequired, "Say why the month closes with deliveries not invoiced (3 to 500 characters).");
        }

        var endsOn = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT ends_on FROM fin.period WHERE company_id = @c AND period_id = @p", r => (DateOnly?)r.Date(0), cancellationToken,
            ("c", context.CompanyId), ("p", command.PeriodId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(SalesErrors.NotFound, "The period does not exist.");

        // The same lines CONTRACT-ASSET reports as UNBILLED_AT_CLOSE at the period's end, and not yet accepted for it.
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            WITH ex AS (SELECT dl.delivery_line_id AS id,
                               round((dl.qty_delivered - dl.qty_invoiced) * ol.unit_price, 2)
                               + coalesce(round((dl.qty_delivered - dl.qty_invoiced) * ol.freight_unit_price, 2), 0) AS a
                        FROM log.delivery_line dl JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id
                        WHERE dl.company_id = @c AND dl.qty_delivered > dl.qty_invoiced),
                 gl AS (SELECT subledger_ref AS id, min(posting_date) AS since FROM fin.gl_entry
                        WHERE company_id = @c AND account_role IN ('CONTRACT_ASSET', 'UNBILLED_RECEIVABLE') GROUP BY subledger_ref)
            SELECT ex.id, ex.a FROM ex JOIN gl ON gl.id = ex.id
            WHERE ex.a > 0 AND gl.since <= @end
              AND NOT EXISTS (SELECT 1 FROM sal.unbilled_acceptance_line al JOIN sal.unbilled_acceptance ac ON ac.acceptance_id = al.acceptance_id
                              WHERE al.delivery_line_id = ex.id AND ac.period_id = @p)
            ORDER BY ex.id
            """,
            r => (Id: r.GetGuid(0), Amount: r.GetDecimal(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("end", endsOn),
            ("p", command.PeriodId)).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            throw new DomainException(NothingToAccept, "The month has no deliveries left to invoice or accept.");
        }

        var user = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var id = context.ResultRef;
        var total = lines.Sum(l => l.Amount);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "UnbilledDeliveriesAccepted", 1, "UnbilledAcceptance", id, 1,
                JsonSerializer.Serialize(new
                {
                    acceptanceId = id,
                    periodId = command.PeriodId,
                    periodEnd = endsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reason,
                    lines = lines.Count,
                    unbilled = total.ToString(CultureInfo.InvariantCulture),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO sal.unbilled_acceptance (acceptance_id, company_id, period_id, reason, accepted_by, accepted_at, event_id) VALUES (@id, @c, @p, @r, @u, @now, @e)",
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("p", command.PeriodId),
            ("r", reason),
            ("u", user),
            ("now", context.Clock.UtcNow),
            ("e", eventId)).ConfigureAwait(false);
        foreach (var (line, amount) in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO sal.unbilled_acceptance_line (acceptance_id, company_id, delivery_line_id, unbilled_amount) VALUES (@a, @c, @l, @m)",
                cancellationToken,
                ("a", id),
                ("c", context.CompanyId),
                ("l", line),
                ("m", amount)).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { acceptanceId = id, lines = lines.Count, unbilled = total.ToString(CultureInfo.InvariantCulture) });
    }
}
