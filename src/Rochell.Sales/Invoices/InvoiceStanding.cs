using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Invoices;

/// <summary>
/// E-VS3-07-11: an issued invoice's commercial status follows its AR document. Open 0 → CREDITED when its CONFIRMED credit notes alone
/// cover the total, else PAID; open &gt; 0 → PARTIALLY_PAID with live applications or withholdings, else CONFIRMED. The caller holds
/// the invoice lock and has already moved the open amount.
/// </summary>
internal static class InvoiceStanding
{
    private sealed record Facts(string Status, decimal Total, decimal Open, decimal Credited, bool Paid);

    public static async Task<string> RefreshAsync(CommandContext context, Guid invoiceId, string commandType, Guid eventId, CancellationToken cancellationToken)
    {
        var facts = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.commercial_status, i.total, a.open_amount,
                   coalesce((SELECT sum(n.total) FROM sal.credit_note n WHERE n.invoice_id = i.invoice_id AND n.commercial_status = 'CONFIRMED'), 0),
                   EXISTS (SELECT 1 FROM fin.ar_application x WHERE x.ar_doc_id = a.ar_doc_id AND x.reverses_application_id IS NULL
                             AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id))
                   OR EXISTS (SELECT 1 FROM fin.customer_withholding w WHERE w.ar_doc_id = a.ar_doc_id AND w.status = 'ACTIVE')
            FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id
            WHERE i.invoice_id = @i
            """,
            r => new Facts(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetBoolean(4)),
            cancellationToken,
            ("i", invoiceId)).ConfigureAwait(false))!;
        var status = facts.Open == 0m
            ? (facts.Credited == facts.Total ? "CREDITED" : "PAID")
            : (facts.Paid ? "PARTIALLY_PAID" : "CONFIRMED");
        if (status == facts.Status)
        {
            return status;
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.invoice SET commercial_status = @s, version = version + 1 WHERE invoice_id = @i", cancellationToken,
            ("s", status), ("i", invoiceId)).ConfigureAwait(false);
        await context.AppendStateAsync(Invoicing.Aggregate, invoiceId, "DOCUMENT", facts.Status, status, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return status;
    }
}
