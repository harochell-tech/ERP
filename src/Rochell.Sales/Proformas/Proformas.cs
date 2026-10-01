using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Tax;

namespace Rochell.Sales.Proformas;

public static class ProformaErrors
{
    public const string MarkInvalid = "PROFORMA_MARK_INVALID";
    public const string Required = "PROFORMA_REQUIRED";
}

/// <summary>One delivered line of a delivery, as its proforma carries it: the quantity that reached the customer at the order price.</summary>
public sealed record DeliveredLine(Guid DeliveryLineId, Guid ItemId, string Uom, decimal Quantity, decimal UnitPrice, decimal Net);

/// <summary>
/// E-FIS1b-1…3, E-FIS1b-01-1…3: the proforma of a delivery — PF-000001, issued in the transaction that delivers the goods of an
/// order marked "exención en trámite". It posts nothing: the unbilled receivable is the delivery's (P-16).
/// </summary>
internal static class Proformas
{
    public const string Aggregate = "Proforma";

    public sealed record Issued(Guid ProformaId, string ProformaNo);

    private sealed record Mark(bool Pending, bool? CollectsItbis);

    /// <summary>E-FIS1b-01-1: the mark and what it collects go together; only a customer with an RNC or cédula can be certified.</summary>
    public static (bool Pending, bool? CollectsItbis) Validate(bool exemptionPending, bool? collectsItbis)
        => exemptionPending == collectsItbis.HasValue
            ? (exemptionPending, collectsItbis)
            : throw new DomainException(
                ProformaErrors.MarkInvalid, "An order with the exemption in process says whether its proformas collect the ITBIS; other orders do not say it (E-FIS1b-01-1).");

    /// <summary>The proforma of this delivery, or null when its order is not marked or nothing reached the customer.</summary>
    public static async Task<Issued?> IssueOnDeliveryAsync(
        CommandContext context, string commandType, Guid salesOrderId, Guid deliveryId, string deliveryNo, Guid partyId, DateOnly deliveredOn, IReadOnlyList<DeliveredLine> lines,
        CancellationToken cancellationToken)
    {
        var mark = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT exemption_pending, proforma_collects_itbis FROM sal.sales_order WHERE company_id = @c AND sales_order_id = @o",
            r => new Mark(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetBoolean(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("o", salesOrderId)).ConfigureAwait(false);
        if (mark is not { Pending: true } || lines.Count == 0)
        {
            return null;
        }

        var days = await SalesSql.ScalarAsync<int?>(
            context, "SELECT payment_terms_days FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p AND status = 'ACTIVE'", cancellationToken,
            ("c", context.CompanyId), ("p", partyId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.TermsRequired, "The customer has no approved terms: its proforma cannot be given a due date (E-FIS1b-01-2).");

        // E-FIS1b-01-3: the ITBIS of the rule in force on the delivery day; a closed fiscal gate refuses the delivery.
        var categories = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT item_id, item_category FROM md.item WHERE company_id = @c AND item_id = ANY(@ids)",
            r => (Id: r.GetGuid(0), Category: r.GetString(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", lines.Select(l => l.ItemId).Distinct().ToArray())).ConfigureAwait(false)).ToDictionary(x => x.Id, x => x.Category);
        var taxes = await TaxEngine.PreviewSalesItbisAsync(
            context.Connection, context.Transaction, context.CompanyId, deliveredOn, [.. lines.Select(l => new TaxableLine(l.DeliveryLineId, categories[l.ItemId], l.Net))],
            cancellationToken).ConfigureAwait(false);
        decimal Itbis(DeliveredLine line) => taxes.Where(t => t.LineId == line.DeliveryLineId && t.Effect == TaxEffects.Output).Sum(t => t.Amount);

        await SalesSql.LockAsync(context, "proforma-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(proforma_no from 4)::int) FROM sal.proforma WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var proformaNo = "PF-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var proformaId = context.Ids.NewId();
        var net = lines.Sum(l => l.Net);
        var itbis = lines.Sum(Itbis);
        var dueDate = deliveredOn.AddDays(days);
        var issuer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ProformaIssued",
                1,
                Aggregate,
                proformaId,
                1,
                JsonSerializer.Serialize(new
                {
                    proformaId,
                    proformaNo,
                    partyId,
                    salesOrderId,
                    deliveryId,
                    deliveryNo,
                    proformaDate = deliveredOn,
                    dueDate,
                    collectsItbis = mark.CollectsItbis!.Value,
                    net = M(net),
                    itbis = M(itbis),
                    total = M(net + itbis),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.proforma (proforma_id, company_id, proforma_no, party_id, sales_order_id, delivery_id, proforma_date, due_date, collects_itbis, net_total, itbis_total,
              total, allocated_amount, status, issue_event_id, issued_by, version)
            VALUES (@id, @c, @no, @p, @o, @d, @date, @due, @collects, @net, @itbis, @total, 0, 'OPEN', @e, @by, 1)
            """,
            cancellationToken,
            ("id", proformaId),
            ("c", context.CompanyId),
            ("no", proformaNo),
            ("p", partyId),
            ("o", salesOrderId),
            ("d", deliveryId),
            ("date", deliveredOn),
            ("due", dueDate),
            ("collects", mark.CollectsItbis.Value),
            ("net", net),
            ("itbis", itbis),
            ("total", net + itbis),
            ("e", eventId),
            ("by", issuer)).ConfigureAwait(false);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO sal.proforma_line (proforma_line_id, company_id, proforma_id, line_no, delivery_line_id, item_id, uom, quantity, unit_price, net_amount, itbis_amount)
                VALUES (@id, @c, @pf, @n, @dl, @item, @uom, @q, @price, @net, @itbis)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("pf", proformaId),
                ("n", i + 1),
                ("dl", line.DeliveryLineId),
                ("item", line.ItemId),
                ("uom", line.Uom),
                ("q", line.Quantity),
                ("price", line.UnitPrice),
                ("net", line.Net),
                ("itbis", Itbis(line))).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Aggregate, proformaId, "DOCUMENT", null, "OPEN", commandType, eventId, cancellationToken).ConfigureAwait(false);
        return new Issued(proformaId, proformaNo);
    }

    private static string M(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
