using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Sales.Queries;

// E-VS3-09-1…6: AR aging and the customer's statement of account, read with sales:read, as JSON or CSV.

public static class ArQueryErrors
{
    public const string PolicyMissing = "POLICY_MISSING";
}

public sealed record GetArAging(Guid CompanyId, Guid SessionId, DateOnly? AsOf = null) : IQuery;

public sealed record ArAgingBuckets(int Bucket1Days, int Bucket2Days, int Bucket3Days);

public sealed record ArAgingDocument(Guid ArDocId, Guid InvoiceId, string InvoiceNo, string? Encf, DateOnly DocDate, DateOnly DueDate, decimal OpenAmount, int DaysOverdue, string Bucket);

/// <summary>E-VS3-09-2: unapplied receipts are shown apart, in the customer's favour; <c>Net</c> = open − unapplied.</summary>
public sealed record ArAgingCustomer(
    Guid CustomerId, string CustomerName, decimal Current, decimal Bucket1, decimal Bucket2, decimal Bucket3, decimal Over, decimal Total, decimal Unapplied, decimal Net,
    IReadOnlyList<ArAgingDocument> Documents);

/// <summary>E-UX4-2: the open amount of every customer per bucket; <c>Total</c> is their sum (the grand total).</summary>
public sealed record ArAgingBucketTotals(decimal Current, decimal Bucket1, decimal Bucket2, decimal Bucket3, decimal Over, decimal Total);

public sealed record ArAging(
    DateOnly AsOf, ArAgingBuckets Buckets, IReadOnlyList<ArAgingCustomer> Customers, decimal Total, decimal Unapplied, decimal Net, ArAgingBucketTotals BucketTotals);

[RequiresPermission("sales:read")]
public sealed class GetArAgingHandler : IQueryHandler<GetArAging>
{
    public const string Current = "CURRENT";
    public const string Bucket1 = "BUCKET_1";
    public const string Bucket2 = "BUCKET_2";
    public const string Bucket3 = "BUCKET_3";
    public const string Over = "OVER";

    public string QueryType => "Sales.GetArAging";

    private sealed record Row(Guid ArDocId, Guid InvoiceId, string InvoiceNo, string? Encf, Guid CustomerId, string CustomerName, DateOnly DocDate, DateOnly DueDate, decimal Open);

    private sealed record Advance(Guid CustomerId, string CustomerName, decimal Unapplied);

    public async Task<string> HandleAsync(GetArAging query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var asOf = query.AsOf ?? BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var buckets = await BucketsAsync(context, asOf, cancellationToken).ConfigureAwait(false);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.ar_doc_id, i.invoice_id, i.invoice_no, i.encf, d.party_id, p.legal_name, d.doc_date, d.due_date, d.open_amount::numeric(19,2)
            FROM fin.ar_document d
            JOIN sal.invoice i ON i.ar_doc_id = d.ar_doc_id
            JOIN md.party p ON p.party_id = d.party_id
            WHERE d.company_id = @c AND d.open_amount > 0
            ORDER BY p.legal_name, d.party_id, d.due_date, d.ar_doc_id
            """,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.NullableString(3), r.GetGuid(4), r.GetString(5), r.Date(6), r.Date(7), r.GetDecimal(8)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var advances = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.party_id, p.legal_name, sum(r.unapplied_amount)::numeric(19,2)
            FROM fin.receipt r JOIN md.party p ON p.party_id = r.party_id
            WHERE r.company_id = @c AND r.status = 'RECORDED' AND r.unapplied_amount > 0
            GROUP BY r.party_id, p.legal_name
            """,
            r => new Advance(r.GetGuid(0), r.GetString(1), r.GetDecimal(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);

        var zero = SalesSql.Zero;
        var customers = rows.Select(r => (r.CustomerId, r.CustomerName)).Concat(advances.Select(a => (a.CustomerId, a.CustomerName))).Distinct()
            .OrderBy(c => c.CustomerName, StringComparer.Ordinal).ThenBy(c => c.CustomerId)
            .Select(c =>
            {
                var documents = rows.Where(r => r.CustomerId == c.CustomerId).Select(r =>
                {
                    var days = asOf.DayNumber - r.DueDate.DayNumber;
                    return new ArAgingDocument(r.ArDocId, r.InvoiceId, r.InvoiceNo, r.Encf, r.DocDate, r.DueDate, r.Open, Math.Max(days, 0), BucketOf(days, buckets));
                }).ToList();
                decimal Sum(string bucket) => zero + documents.Where(d => d.Bucket == bucket).Sum(d => d.OpenAmount);
                var total = zero + documents.Sum(d => d.OpenAmount);
                var unapplied = zero + advances.Where(a => a.CustomerId == c.CustomerId).Sum(a => a.Unapplied);
                return new ArAgingCustomer(c.CustomerId, c.CustomerName, Sum(Current), Sum(Bucket1), Sum(Bucket2), Sum(Bucket3), Sum(Over), total, unapplied, total - unapplied, documents);
            }).ToList();
        var all = zero + customers.Sum(c => c.Total);
        var favour = zero + customers.Sum(c => c.Unapplied);
        var totals = new ArAgingBucketTotals(
            zero + customers.Sum(c => c.Current), zero + customers.Sum(c => c.Bucket1), zero + customers.Sum(c => c.Bucket2), zero + customers.Sum(c => c.Bucket3),
            zero + customers.Sum(c => c.Over), all);
        return ApiJson.Serialize(new ArAging(asOf, buckets, customers, all, favour, all - favour, totals));
    }

    public static string BucketOf(int daysOverdue, ArAgingBuckets buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        return daysOverdue <= 0 ? Current
            : daysOverdue <= buckets.Bucket1Days ? Bucket1
            : daysOverdue <= buckets.Bucket2Days ? Bucket2
            : daysOverdue <= buckets.Bucket3Days ? Bucket3
            : Over;
    }

    /// <summary>The CREDIT policy version in force at the date; none, or buckets not increasing, is refused loudly (E-VS3-09-1).</summary>
    private static async Task<ArAgingBuckets> BucketsAsync(QueryContext context, DateOnly asOf, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT p.param_code, p.value #>> '{}' FROM acc.accounting_policy_version v
            JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'CREDIT' AND v.status = 'ACTIVE' AND p.param_code LIKE 'ar_aging_bucket_%'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            ("c", context.CompanyId),
            ("d", asOf)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values[reader.GetString(0)] = int.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        if (!values.TryGetValue("ar_aging_bucket_1_days", out var b1) || !values.TryGetValue("ar_aging_bucket_2_days", out var b2)
            || !values.TryGetValue("ar_aging_bucket_3_days", out var b3))
        {
            throw new DomainException(ArQueryErrors.PolicyMissing, $"No ACTIVE CREDIT policy with the AR aging buckets on {asOf:yyyy-MM-dd} (E-VS3-09-1).");
        }

        return b1 < b2 && b2 < b3
            ? new ArAgingBuckets(b1, b2, b3)
            : throw new DomainException(ArQueryErrors.PolicyMissing, $"The CREDIT policy's AR aging buckets must increase ({b1}, {b2}, {b3}).");
    }
}

public sealed record GetCustomerStatement(Guid CompanyId, Guid SessionId, Guid PartyId, DateOnly From, DateOnly To) : IQuery;

/// <summary>One journal of the customer's AR_CONTROL and UNAPPLIED_RECEIPTS in the range (net of both), with its document.</summary>
public sealed record StatementEntry(DateOnly PostingDate, string Kind, string? DocumentNo, string EventType, decimal Debit, decimal Credit, decimal Balance);

public sealed record CustomerStatement(
    Guid PartyId, string CustomerName, string? Rnc, DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<StatementEntry> Entries, decimal TotalDebit, decimal TotalCredit, decimal Closing);

/// <summary>
/// E-VS3-09-3: built from the customer's entries in AR_CONTROL and UNAPPLIED_RECEIPTS, so it always agrees with AR-GL. A receipt's
/// application nets to zero inside the customer and is left out; so is its unapply.
/// </summary>
[RequiresPermission("sales:read")]
public sealed class GetCustomerStatementHandler : IQueryHandler<GetCustomerStatement>
{
    /// <summary>E-VS3-09-3: at most a year and a day per statement.</summary>
    public const int MaxDays = 366;

    public string QueryType => "Sales.GetCustomerStatement";

    private sealed record Customer(string Name, string? Rnc);

    private sealed record Movement(DateOnly PostingDate, string EventType, string Kind, string? DocumentNo, decimal Net);

    public async Task<string> HandleAsync(GetCustomerStatement query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.To < query.From || query.To.DayNumber - query.From.DayNumber >= MaxDays)
        {
            throw new DomainException(QueryErrors.InvalidParameter, $"The range goes forward and covers at most {MaxDays} days (E-VS3-09-3).");
        }

        var customer = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT legal_name, rnc FROM md.party WHERE company_id = @c AND party_id = @p AND is_customer",
            r => new Customer(r.GetString(0), r.NullableString(1)), cancellationToken, ("c", context.CompanyId), ("p", query.PartyId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The customer does not exist.");
        await using var openingCommand = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce(sum(debit - credit), 0)::numeric(19,2) FROM fin.gl_entry
            WHERE company_id = @c AND party_id = @p AND account_role IN ('AR_CONTROL', 'UNAPPLIED_RECEIPTS') AND posting_date < @from
            """,
            ("c", context.CompanyId),
            ("p", query.PartyId),
            ("from", query.From));
        var opening = (decimal)(await openingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        var movements = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.posting_date, ev.event_type,
                   CASE ev.event_type WHEN 'InvoiceIssued' THEN 'FACTURA' WHEN 'InvoiceVoided' THEN 'FACTURA_ANULADA' WHEN 'CreditNoteIssued' THEN 'NOTA_DE_CREDITO'
                        WHEN 'ReceiptRecorded' THEN 'COBRO' WHEN 'ReceiptReversed' THEN 'COBRO_ANULADO' WHEN 'ReceiptBounced' THEN 'CHEQUE_DEVUELTO'
                        WHEN 'WithholdingByCustomer' THEN 'RETENCION' WHEN 'CustomerWithholdingReversed' THEN 'RETENCION_REVERSADA' ELSE 'OTRO' END,
                   coalesce(i.invoice_no, iv.invoice_no, n.credit_note_no, r.receipt_no, rc.receipt_no,
                            (SELECT x.invoice_no FROM sal.invoice x WHERE x.invoice_id = w.invoice_id),
                            (SELECT x.invoice_no FROM sal.invoice x WHERE x.invoice_id = wr.invoice_id)),
                   m.net
            FROM (SELECT j.journal_id, j.source_event_id, e.posting_date, sum(e.debit - e.credit)::numeric(19,2) AS net
                  FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                  WHERE e.company_id = @c AND e.party_id = @p AND e.account_role IN ('AR_CONTROL', 'UNAPPLIED_RECEIPTS') AND e.posting_date BETWEEN @from AND @to
                  GROUP BY j.journal_id, j.source_event_id, e.posting_date
                  HAVING sum(e.debit - e.credit) <> 0) m
            JOIN core.domain_event ev ON ev.company_id = @c AND ev.event_id = m.source_event_id
            LEFT JOIN sal.invoice i ON i.posting_event_id = m.source_event_id
            LEFT JOIN sal.invoice iv ON iv.void_event_id = m.source_event_id
            LEFT JOIN sal.credit_note n ON n.posting_event_id = m.source_event_id
            LEFT JOIN fin.receipt r ON r.posting_event_id = m.source_event_id
            LEFT JOIN fin.receipt rc ON rc.closing_event_id = m.source_event_id
            LEFT JOIN fin.customer_withholding w ON w.posting_event_id = m.source_event_id
            LEFT JOIN fin.customer_withholding wr ON wr.reversal_event_id = m.source_event_id
            ORDER BY m.posting_date, ev.recorded_at, m.journal_id
            """,
            r => new Movement(r.Date(0), r.GetString(1), r.GetString(2), r.NullableString(3), r.GetDecimal(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId),
            ("from", query.From),
            ("to", query.To)).ConfigureAwait(false);

        var zero = SalesSql.Zero;
        var balance = opening;
        var entries = new List<StatementEntry>();
        foreach (var m in movements)
        {
            balance += m.Net;
            entries.Add(new StatementEntry(m.PostingDate, m.Kind, m.DocumentNo, m.EventType, m.Net > 0m ? m.Net : zero, m.Net < 0m ? -m.Net : zero, balance));
        }

        return ApiJson.Serialize(new CustomerStatement(
            query.PartyId, customer.Name, customer.Rnc, query.From, query.To, opening, entries, zero + entries.Sum(e => e.Debit), zero + entries.Sum(e => e.Credit), balance));
    }
}

/// <summary>E-VS3-09-4: CSV of the AR aging and the statement of account, from the query's JSON (same format as the ledger reports).</summary>
public static class ArCsv
{
    public static string Aging(string json)
    {
        var a = System.Text.Json.JsonSerializer.Deserialize<ArAging>(json, ApiJson.Options) ?? throw new InvalidOperationException("Empty query result.");
        var csv = new Finance.Ledger.LedgerCsv.Writer("Cliente", "Factura", "e-NCF", "Fecha", "Vence", "Días vencidos", "Tramo", "Abierto");
        foreach (var c in a.Customers)
        {
            foreach (var d in c.Documents)
            {
                csv.Row(c.CustomerName, d.InvoiceNo, d.Encf, D(d.DocDate), D(d.DueDate), d.DaysOverdue.ToString(CultureInfo.InvariantCulture), d.Bucket, M(d.OpenAmount));
            }

            if (c.Unapplied != 0m)
            {
                csv.Row(c.CustomerName, "A favor (cobros no aplicados)", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(-c.Unapplied));
            }

            csv.Row(c.CustomerName, "Saldo neto", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(c.Net));
        }

        csv.Row("Total", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(a.Net));
        return csv.ToString();
    }

    public static string Statement(string json)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<CustomerStatement>(json, ApiJson.Options) ?? throw new InvalidOperationException("Empty query result.");
        var csv = new Finance.Ledger.LedgerCsv.Writer("Fecha", "Tipo", "Documento", "Débito", "Crédito", "Saldo");
        csv.Row(D(s.From), "Saldo inicial", string.Empty, string.Empty, string.Empty, M(s.Opening));
        foreach (var e in s.Entries)
        {
            csv.Row(D(e.PostingDate), e.Kind, e.DocumentNo, M(e.Debit), M(e.Credit), M(e.Balance));
        }

        csv.Row(D(s.To), "Saldo final", string.Empty, M(s.TotalDebit), M(s.TotalCredit), M(s.Closing));
        return csv.ToString();
    }

    private static string M(decimal value) => decimal.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture);

    private static string D(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
