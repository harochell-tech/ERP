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

/// <summary>
/// E-FIS1b-3: an open proforma — goods delivered and not yet fiscally invoiced. <c>Balance</c> is what it still collects;
/// <c>Deposit</c> what the customer paid above its net (the ITBIS advanced).
/// </summary>
public sealed record ArAgingProforma(
    Guid ProformaId, string ProformaNo, string DeliveryNo, DateOnly ProformaDate, DateOnly DueDate, decimal Balance, decimal Deposit, int DaysOverdue, string Bucket);

/// <summary>E-VS3-09-2: unapplied receipts are shown apart, in the customer's favour; <c>Net</c> = open − unapplied.</summary>
/// <remarks>
/// E-FIS1b-3: <c>Proformas</c> (the balance of the open proformas) and <c>Deposits</c> are a column of their own, outside
/// <c>Total</c> and <c>Net</c>; <c>Unapplied</c> leaves out what is allocated to proformas.
/// </remarks>
public sealed record ArAgingCustomer(
    Guid CustomerId, string CustomerName, decimal Current, decimal Bucket1, decimal Bucket2, decimal Bucket3, decimal Over, decimal Total, decimal Unapplied, decimal Net,
    IReadOnlyList<ArAgingDocument> Documents, decimal Proformas, decimal Deposits, IReadOnlyList<ArAgingProforma> ProformaDocuments);

/// <summary>E-UX4-2: the open amount of every customer per bucket; <c>Total</c> is their sum (the grand total).</summary>
public sealed record ArAgingBucketTotals(decimal Current, decimal Bucket1, decimal Bucket2, decimal Bucket3, decimal Over, decimal Total);

public sealed record ArAging(
    DateOnly AsOf, ArAgingBuckets Buckets, IReadOnlyList<ArAgingCustomer> Customers, decimal Total, decimal Unapplied, decimal Net, ArAgingBucketTotals BucketTotals,
    decimal Proformas, decimal Deposits);

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

    private sealed record Proforma(Guid ProformaId, string ProformaNo, string DeliveryNo, Guid CustomerId, string CustomerName, DateOnly Date, DateOnly DueDate, decimal Balance, decimal Deposit);

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
            SELECT r.party_id, p.legal_name, sum(r.unapplied_amount - r.allocated_amount)::numeric(19,2)
            FROM fin.receipt r JOIN md.party p ON p.party_id = r.party_id
            WHERE r.company_id = @c AND r.status = 'RECORDED' AND r.unapplied_amount > r.allocated_amount
            GROUP BY r.party_id, p.legal_name
            """,
            r => new Advance(r.GetGuid(0), r.GetString(1), r.GetDecimal(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var proformas = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT pf.proforma_id, pf.proforma_no, d.delivery_no, pf.party_id, p.legal_name, pf.proforma_date, pf.due_date,
                   ((CASE WHEN pf.collects_itbis THEN pf.total ELSE pf.net_total END) - pf.allocated_amount)::numeric(19,2),
                   greatest(pf.allocated_amount - pf.net_total, 0)::numeric(19,2)
            FROM sal.proforma pf
            JOIN md.party p ON p.party_id = pf.party_id
            JOIN log.delivery d ON d.delivery_id = pf.delivery_id
            WHERE pf.company_id = @c AND pf.status = 'OPEN'
              AND (pf.allocated_amount < CASE WHEN pf.collects_itbis THEN pf.total ELSE pf.net_total END OR pf.allocated_amount > pf.net_total)
            ORDER BY p.legal_name, pf.party_id, pf.due_date, pf.proforma_no
            """,
            r => new Proforma(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.Date(5), r.Date(6), r.GetDecimal(7), r.GetDecimal(8)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);

        var zero = SalesSql.Zero;
        var customers = rows.Select(r => (r.CustomerId, r.CustomerName)).Concat(advances.Select(a => (a.CustomerId, a.CustomerName)))
            .Concat(proformas.Select(f => (f.CustomerId, f.CustomerName))).Distinct()
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
                var pending = proformas.Where(f => f.CustomerId == c.CustomerId).Select(f =>
                {
                    var days = f.Balance > 0m ? asOf.DayNumber - f.DueDate.DayNumber : 0;
                    return new ArAgingProforma(f.ProformaId, f.ProformaNo, f.DeliveryNo, f.Date, f.DueDate, f.Balance, f.Deposit, Math.Max(days, 0), BucketOf(days, buckets));
                }).ToList();
                return new ArAgingCustomer(
                    c.CustomerId, c.CustomerName, Sum(Current), Sum(Bucket1), Sum(Bucket2), Sum(Bucket3), Sum(Over), total, unapplied, total - unapplied, documents,
                    zero + pending.Sum(f => f.Balance), zero + pending.Sum(f => f.Deposit), pending);
            }).ToList();
        var all = zero + customers.Sum(c => c.Total);
        var favour = zero + customers.Sum(c => c.Unapplied);
        var totals = new ArAgingBucketTotals(
            zero + customers.Sum(c => c.Current), zero + customers.Sum(c => c.Bucket1), zero + customers.Sum(c => c.Bucket2), zero + customers.Sum(c => c.Bucket3),
            zero + customers.Sum(c => c.Over), all);
        return ApiJson.Serialize(new ArAging(asOf, buckets, customers, all, favour, all - favour, totals, zero + customers.Sum(c => c.Proformas), zero + customers.Sum(c => c.Deposits)));
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

/// <summary>E-FIS1b-3: an open proforma of the customer today — delivered, collected on the proforma, not yet fiscally invoiced.</summary>
public sealed record StatementProforma(Guid ProformaId, string ProformaNo, string DeliveryNo, DateOnly ProformaDate, DateOnly DueDate, decimal Total, decimal Allocated, decimal Balance, decimal Deposit);

/// <remarks>
/// E-FIS1b-3: <c>OpenProformas</c> and <c>ProformaBalance</c> are apart from the ledger balance — they are today's open
/// proformas, whose receipts appear above as unapplied collections until the invoice is issued (option A).
/// </remarks>
public sealed record CustomerStatement(
    Guid PartyId, string CustomerName, string? Rnc, DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<StatementEntry> Entries, decimal TotalDebit, decimal TotalCredit, decimal Closing,
    IReadOnlyList<StatementProforma> OpenProformas, decimal ProformaBalance);

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
                        WHEN 'WithholdingByCustomer' THEN 'RETENCION' WHEN 'CustomerWithholdingReversed' THEN 'RETENCION_REVERSADA'
                        WHEN 'CustomerRefundReleased' THEN 'DEVOLUCION' ELSE 'OTRO' END,
                   coalesce(i.invoice_no, iv.invoice_no, n.credit_note_no, r.receipt_no, rc.receipt_no, rf.refund_no,
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
            LEFT JOIN fin.customer_refund rf ON rf.posting_event_id = m.source_event_id -- FIS1b-07: the refund of a credit balance (P-36)
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

        var proformas = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT pf.proforma_id, pf.proforma_no, d.delivery_no, pf.proforma_date, pf.due_date, pf.total::numeric(19,2), pf.allocated_amount::numeric(19,2),
                   ((CASE WHEN pf.collects_itbis THEN pf.total ELSE pf.net_total END) - pf.allocated_amount)::numeric(19,2),
                   greatest(pf.allocated_amount - pf.net_total, 0)::numeric(19,2)
            FROM sal.proforma pf JOIN log.delivery d ON d.delivery_id = pf.delivery_id
            WHERE pf.company_id = @c AND pf.party_id = @p AND pf.status = 'OPEN'
            ORDER BY pf.proforma_no
            """,
            r => new StatementProforma(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Date(3), r.Date(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomerStatement(
            query.PartyId, customer.Name, customer.Rnc, query.From, query.To, opening, entries, zero + entries.Sum(e => e.Debit), zero + entries.Sum(e => e.Credit), balance,
            proformas, zero + proformas.Sum(f => f.Balance)));
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

            // E-FIS1b-3: the open proformas, apart from the invoiced balance.
            foreach (var f in c.ProformaDocuments)
            {
                csv.Row(c.CustomerName, f.ProformaNo, "Proforma (sin e-CF)", D(f.ProformaDate), D(f.DueDate), f.DaysOverdue.ToString(CultureInfo.InvariantCulture), f.Bucket, M(f.Balance));
            }

            if (c.Deposits != 0m)
            {
                csv.Row(c.CustomerName, "Depósito del cliente (ITBIS adelantado en proformas)", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(-c.Deposits));
            }
        }

        csv.Row("Total", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(a.Net));
        if (a.Proformas != 0m || a.Deposits != 0m)
        {
            csv.Row("Total proformas pendientes de e-CF", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(a.Proformas));
        }

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
        foreach (var f in s.OpenProformas)
        {
            csv.Row(D(f.ProformaDate), "PROFORMA_SIN_ECF", f.ProformaNo, M(f.Total), M(f.Allocated), M(f.Balance));
        }

        return csv.ToString();
    }

    private static string M(decimal value) => decimal.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture);

    private static string D(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
