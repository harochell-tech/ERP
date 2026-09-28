using System.Globalization;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Receipts;

public static class ReceiptErrors
{
    public const string MethodInvalid = "RECEIPT_METHOD_INVALID";
    public const string CustomerNotActive = "CUSTOMER_NOT_ACTIVE";
    public const string BankAccountInvalid = "BANK_ACCOUNT_INVALID";
    public const string DateInvalid = "RECEIPT_DATE_INVALID";
    public const string NotDepositable = "RECEIPT_NOT_DEPOSITABLE";
    public const string ExceedsUnapplied = "APPLICATION_EXCEEDS_UNAPPLIED";
    public const string ExceedsOpen = "APPLICATION_EXCEEDS_OPEN";
    public const string InvoiceNotOpen = "INVOICE_NOT_OPEN";
    public const string OtherCustomer = "INVOICE_OF_ANOTHER_CUSTOMER";
    public const string ApplicationNotFound = "APPLICATION_NOT_FOUND";
    public const string NotBounceable = "RECEIPT_NOT_BOUNCEABLE";
    public const string NotReversible = "RECEIPT_NOT_REVERSIBLE";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string WithholdingKindInvalid = "WITHHOLDING_KIND_INVALID";
    public const string WithholdingExceeds = "WITHHOLDING_EXCEEDS";
    public const string CertificateDuplicate = "WITHHOLDING_CERTIFICATE_DUPLICATE";
}

/// <summary>Shared reads and locks of the receipt commands. Lock order (VS#3 §5): invoices → AR documents (by id) → receipt → bank account.</summary>
internal static class Receipting
{
    public const string Aggregate = "Receipt";
    public const string DepositAggregate = "ReceiptDeposit";

    public sealed record Row(
        string No, Guid PartyId, string Method, decimal Amount, DateOnly ValueDate, Guid? BankAccountId, string Status, string Application, string Bank, decimal Unapplied,
        Guid? DepositId, Guid PostingEventId, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid receiptId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT receipt_no, party_id, method, amount::numeric(19,2), value_date, bank_account_id, status, application_status, bank_status, unapplied_amount::numeric(19,2),
                   deposit_id, posting_event_id, version
            FROM fin.receipt WHERE company_id = @c AND receipt_id = @r FOR UPDATE
            """,
            r => new Row(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.Date(4), r.NullableGuid(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetDecimal(9),
                r.NullableGuid(10), r.GetGuid(11), r.GetInt64(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", receiptId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The receipt does not exist.");
        return expectedVersion is null || row.Version == expectedVersion
            ? row
            : throw new DomainException(SalesErrors.VersionConflict, $"The receipt changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    public static string ApplicationStatus(decimal amount, decimal unapplied)
        => unapplied == amount ? "UNAPPLIED" : unapplied == 0m ? "APPLIED" : "PARTIALLY_APPLIED";

    public sealed record Invoice(Guid InvoiceId, Guid PartyId, string InvoiceNo, string Commercial, Guid ArDocId, DateOnly InvoiceDate, decimal TaxTotal);

    /// <summary>Locks issued invoices in id order, then their AR documents in id order; returns them with the open amounts.</summary>
    public static async Task<(List<Invoice> Invoices, Dictionary<Guid, decimal> Open)> LockInvoicesAsync(CommandContext context, IEnumerable<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var invoices = new List<Invoice>();
        foreach (var id in invoiceIds.Distinct().Order())
        {
            invoices.Add(await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT invoice_id, party_id, invoice_no, commercial_status, ar_doc_id, invoice_date, tax_total::numeric(19,2)
                FROM sal.invoice WHERE company_id = @c AND invoice_id = @i AND ar_doc_id IS NOT NULL FOR UPDATE
                """,
                r => new Invoice(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.Date(5), r.GetDecimal(6)),
                cancellationToken,
                ("c", context.CompanyId),
                ("i", id)).ConfigureAwait(false)
                ?? throw new DomainException(SalesErrors.NotFound, "The invoice does not exist or is not issued."));
        }

        var open = new Dictionary<Guid, decimal>();
        foreach (var doc in invoices.Select(i => i.ArDocId).Order())
        {
            open[doc] = (await SalesSql.ScalarAsync<decimal?>(
                context, "SELECT open_amount::numeric(19,2) FROM fin.ar_document WHERE ar_doc_id = @a FOR UPDATE", cancellationToken, ("a", doc)).ConfigureAwait(false))!.Value;
        }

        return (invoices, open);
    }

    public static Task MoveOpenAsync(CommandContext context, Guid arDocId, decimal delta, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.ar_document SET open_amount = open_amount + @d, version = version + 1 WHERE ar_doc_id = @a", cancellationToken,
            ("d", delta), ("a", arDocId));

    /// <summary>A live application: never unapplied (no mirror row).</summary>
    public sealed record Application(Guid ApplicationId, Guid ArDocId, Guid InvoiceId, string InvoiceNo, decimal Amount, Guid EventId);

    public static Task<List<Application>> LiveApplicationsAsync(CommandContext context, Guid receiptId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.application_id, x.ar_doc_id, i.invoice_id, i.invoice_no, x.amount::numeric(19,2), x.event_id
            FROM fin.ar_application x JOIN sal.invoice i ON i.ar_doc_id = x.ar_doc_id
            WHERE x.receipt_id = @r AND x.reverses_application_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id)
            ORDER BY x.event_id, x.application_id
            """,
            r => new Application(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetGuid(5)),
            cancellationToken,
            ("r", receiptId));

    /// <summary>
    /// Undoes the live applications of one ReceiptApplied event (E-VS3-07-6): a ReceiptUnapplied event, the exact reversal of its P-25
    /// journal, the mirror rows and the open amounts back. Returns the amount given back to the receipt and the event.
    /// </summary>
    public static async Task<(decimal Amount, Guid EventId)> UndoAsync(
        CommandContext context, PostingEngine engine, Guid receiptId, Row receipt, IReadOnlyList<Application> applications, long version, string reason, Guid? causation,
        CancellationToken cancellationToken)
    {
        var journal = await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT journal_id FROM fin.gl_journal j WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("e", applications[0].EventId)).ConfigureAwait(false)
            ?? throw new DomainException(ReceiptErrors.ApplicationNotFound, "The application has no live journal.");
        var today = SalesSql.Today(context);
        var plan = await engine.PrepareReversalAsync(context, journal, today, cancellationToken).ConfigureAwait(false);
        var amount = applications.Sum(a => a.Amount);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptUnapplied",
                1,
                Aggregate,
                receiptId,
                version,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    receiptId,
                    receiptNo = receipt.No,
                    applicationEventId = applications[0].EventId,
                    invoices = applications.Select(a => new { invoiceId = a.InvoiceId, invoiceNo = a.InvoiceNo, amount = Money(a.Amount) }),
                    amount = Money(amount),
                    reason,
                }),
                Publish: true,
                BusinessDate: today,
                CausationId: causation),
            cancellationToken).ConfigureAwait(false);
        await engine.WriteReversalAsync(context, plan, eventId, context.Clock.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (var a in applications)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.ar_application (application_id, company_id, receipt_id, ar_doc_id, amount, event_id, reverses_application_id)
                VALUES (@id, @c, @r, @d, @a, @e, @original)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("r", receiptId),
                ("d", a.ArDocId),
                ("a", a.Amount),
                ("e", eventId),
                ("original", a.ApplicationId)).ConfigureAwait(false);
            await MoveOpenAsync(context, a.ArDocId, a.Amount, cancellationToken).ConfigureAwait(false);
        }

        return (amount, eventId);
    }

    public static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Reason(string? reason)
        => SalesSql.Optional(reason, 500, "The reason") ?? throw new DomainException(ReceiptErrors.ReasonRequired, "A reason is required.");
}
