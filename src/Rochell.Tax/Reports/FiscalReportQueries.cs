using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Tax.Reports;

// FIS2-02 (E-FIS2-02-1…12): the 606 of a month and the informative IT-1 / IR-17 summaries, read with fiscal_report:read. Periods are
// "AAAAMM" as the DGII writes them.

internal static class FiscalPeriods
{
    public static DateOnly Parse(string? period)
        => period is { Length: 6 } p && DateOnly.TryParseExact(p + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            ? first
            : throw new DomainException(QueryErrors.InvalidParameter, "period is AAAAMM, e.g. 202609.");
}

public sealed record GetReport606(Guid CompanyId, Guid SessionId, string Period) : IQuery;

/// <summary>
/// A 606 record in the DGII tool's field order (1–23), plus <paramref name="RecordKind"/> (NCF: the invoice's month; PAYMENT: a
/// withholding paid in this month for an earlier NCF, E-FIS2-02-11), the supplier's name and the record's warnings.
/// </summary>
public sealed record Report606Record(
    Guid SupplierInvoiceId, string RecordKind, string SupplierName, string? Rnc, int? IdType, string? GoodsType, string Ncf, string? NcfModified, DateOnly NcfDate,
    DateOnly? PaymentDate, decimal ServicesAmount, decimal GoodsAmount, decimal TotalAmount, decimal ItbisBilled, decimal ItbisWithheld, decimal ItbisProportional,
    decimal ItbisToCost, decimal ItbisToAdvance, decimal ItbisPerceived, string? IsrWithholdingType, decimal IsrWithheld, decimal IsrPerceived, decimal SelectiveTax,
    decimal OtherTaxes, decimal LegalTip, int PaymentMethod, IReadOnlyList<string> Warnings);

/// <summary>The 606 header (RNC, period, record count, total invoiced) and its records; the tool also needs what the system does not hold (E-FIS2-11).</summary>
public sealed record Report606(string CompanyRnc, string Period, int RecordCount, decimal TotalAmount, IReadOnlyList<Report606Record> Records);

[RequiresPermission("fiscal_report:read")]
public sealed class GetReport606Handler : IQueryHandler<GetReport606>
{
    public string QueryType => "Tax.GetReport606";

    public async Task<string> HandleAsync(GetReport606 query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var month = FiscalPeriods.Parse(query.Period);
        var rnc = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT rnc FROM md.company WHERE company_id = @c", r => r.GetString(0), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? string.Empty;
        var records = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT * FROM tax.report_606(@c, @m)",
            r => new Report606Record(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.NullableString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.NullableString(5), r.GetString(6), r.NullableString(7),
                r.Date(8), r.IsDBNull(9) ? null : r.Date(9), r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12), r.GetDecimal(13), r.GetDecimal(14), r.GetDecimal(15),
                r.GetDecimal(16), r.GetDecimal(17), r.GetDecimal(18), r.NullableString(19), r.GetDecimal(20), r.GetDecimal(21), r.GetDecimal(22), r.GetDecimal(23),
                r.GetDecimal(24), r.GetInt32(25), r.GetFieldValue<string[]>(26)),
            cancellationToken,
            ("c", context.CompanyId),
            ("m", month)).ConfigureAwait(false);
        return ApiJson.Serialize(new Report606(rnc, query.Period, records.Count, records.Sum(r => r.TotalAmount), records));
    }
}

/// <summary>
/// E-FIS2-02-5: the 606 records as CSV rows in the DGII tool's column order (fields 1–23), without a header row (the header — RNC,
/// period, record count — is typed in the tool), dates AAAAMMDD, decimal point, no thousands separator; zero amounts are empty.
/// </summary>
public static class Report606Csv
{
    public static string Build(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var text = new StringBuilder();
        foreach (var r in document.RootElement.GetProperty("records").EnumerateArray())
        {
            string S(string name) => r.GetProperty(name).ValueKind == JsonValueKind.Null ? string.Empty : r.GetProperty(name).ToString();
            string D(string name) => r.GetProperty(name).ValueKind == JsonValueKind.Null ? string.Empty : r.GetProperty(name).GetString()!.Replace("-", string.Empty, StringComparison.Ordinal);
            string A(string name) => decimal.Parse(r.GetProperty(name).GetString()!, CultureInfo.InvariantCulture) == 0m ? string.Empty : r.GetProperty(name).GetString()!;
            text.AppendJoin(',', [
                S("rnc"), S("idType"), S("goodsType"), S("ncf"), S("ncfModified"), D("ncfDate"), D("paymentDate"),
                A("servicesAmount"), A("goodsAmount"), A("totalAmount"), A("itbisBilled"), A("itbisWithheld"), A("itbisProportional"), A("itbisToCost"),
                A("itbisToAdvance"), A("itbisPerceived"), S("isrWithholdingType"), A("isrWithheld"), A("isrPerceived"), A("selectiveTax"), A("otherTaxes"),
                A("legalTip"), S("paymentMethod"),
            ]).Append("\r\n");
        }

        return text.ToString();
    }
}

public sealed record GetIt1Summary(Guid CompanyId, Guid SessionId, string Period) : IQuery;

public sealed record It1SalesLine(string EcfType, int Invoices, decimal TaxedNet, decimal ExemptNet, decimal Itbis);

public sealed record It1Withholding(string Kind, int Count, decimal Amount);

/// <summary>E-FIS2-02-6: what the IT-1 is prepared from — sales by e-CF type, credit notes, the 606's purchase ITBIS, customer withholdings.</summary>
public sealed record It1Summary(
    string Period, IReadOnlyList<It1SalesLine> Sales, int CreditNotes, decimal CreditNotesNet, decimal CreditNotesItbis, decimal PurchaseItbisBilled,
    decimal PurchaseItbisToCost, decimal PurchaseItbisToAdvance, IReadOnlyList<It1Withholding> CustomerWithholdings);

[RequiresPermission("fiscal_report:read")]
public sealed class GetIt1SummaryHandler : IQueryHandler<GetIt1Summary>
{
    public string QueryType => "Tax.GetIt1Summary";

    public async Task<string> HandleAsync(GetIt1Summary query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var month = FiscalPeriods.Parse(query.Period);
        (string, object?)[] args = [("c", context.CompanyId), ("m0", month), ("m1", month.AddMonths(1))];
        var sales = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.ecf_type, count(DISTINCT i.invoice_id)::int,
                   coalesce(sum(l.net_amount) FILTER (WHERE EXISTS (SELECT 1 FROM tax.tax_determination_line d
                       WHERE d.determination_id = i.tax_determination_id AND d.subject_line_id = l.invoice_line_id AND d.effect = 'OUTPUT')), 0)::numeric(19,2),
                   coalesce(sum(l.net_amount) FILTER (WHERE NOT EXISTS (SELECT 1 FROM tax.tax_determination_line d
                       WHERE d.determination_id = i.tax_determination_id AND d.subject_line_id = l.invoice_line_id AND d.effect = 'OUTPUT')), 0)::numeric(19,2),
                   (SELECT coalesce(sum(x.tax_total), 0) FROM sal.invoice x
                    WHERE x.company_id = @c AND x.ecf_type = i.ecf_type AND x.commercial_status NOT IN ('DRAFT', 'VOIDED')
                      AND x.invoice_date >= @m0 AND x.invoice_date < @m1)::numeric(19,2)
            FROM sal.invoice i JOIN sal.invoice_line l ON l.invoice_id = i.invoice_id
            WHERE i.company_id = @c AND i.commercial_status NOT IN ('DRAFT', 'VOIDED') AND i.invoice_date >= @m0 AND i.invoice_date < @m1
            GROUP BY i.ecf_type ORDER BY i.ecf_type
            """,
            r => new It1SalesLine(r.GetString(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)),
            cancellationToken,
            args).ConfigureAwait(false);
        var notes = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*)::int, coalesce(sum(net_total), 0)::numeric(19,2), coalesce(sum(tax_total), 0)::numeric(19,2)
            FROM sal.credit_note WHERE company_id = @c AND commercial_status NOT IN ('DRAFT', 'VOIDED') AND credit_date >= @m0 AND credit_date < @m1
            """,
            r => (Count: r.GetInt32(0), Net: r.GetDecimal(1), Itbis: r.GetDecimal(2)),
            cancellationToken,
            args).ConfigureAwait(false)).Single();
        var purchases = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce(sum(itbis_billed), 0)::numeric(19,2), coalesce(sum(itbis_to_cost), 0)::numeric(19,2), coalesce(sum(itbis_to_advance), 0)::numeric(19,2)
            FROM tax.report_606(@c, @m0) WHERE record_kind = 'NCF'
            """,
            r => (Billed: r.GetDecimal(0), Cost: r.GetDecimal(1), Advance: r.GetDecimal(2)),
            cancellationToken,
            args).ConfigureAwait(false)).Single();
        var withholdings = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT kind, count(*)::int, sum(amount)::numeric(19,2) FROM fin.customer_withholding
            WHERE company_id = @c AND status = 'ACTIVE' AND withholding_date >= @m0 AND withholding_date < @m1 GROUP BY kind ORDER BY kind
            """,
            r => new It1Withholding(r.GetString(0), r.GetInt32(1), r.GetDecimal(2)),
            cancellationToken,
            args).ConfigureAwait(false);
        return ApiJson.Serialize(new It1Summary(query.Period, sales, notes.Count, notes.Net, notes.Itbis, purchases.Billed, purchases.Cost, purchases.Advance, withholdings));
    }
}

public sealed record GetIr17Summary(Guid CompanyId, Guid SessionId, string Period) : IQuery;

/// <summary>One kind of withholding made to suppliers: <paramref name="Tax"/> ITBIS or ISR (with its 606 type), records, base and amount.</summary>
public sealed record Ir17Line(string Tax, string? IsrWithholdingType, int Records, decimal Base, decimal Amount);

/// <summary>E-FIS2-02-7: the withholdings made to suppliers and paid in the month — the 606 records carrying them.</summary>
public sealed record Ir17Summary(string Period, IReadOnlyList<Ir17Line> Lines, decimal ItbisWithheld, decimal IsrWithheld);

[RequiresPermission("fiscal_report:read")]
public sealed class GetIr17SummaryHandler : IQueryHandler<GetIr17Summary>
{
    public string QueryType => "Tax.GetIr17Summary";

    public async Task<string> HandleAsync(GetIr17Summary query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var month = FiscalPeriods.Parse(query.Period);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT CASE v.definition ->> 'base' WHEN 'ITBIS' THEN 'ITBIS' ELSE 'ISR' END AS tax,
                   CASE WHEN v.definition ->> 'base' = 'NET' THEN v.definition ->> 'isr_withholding_type' END AS isr_type,
                   count(DISTINCT r.si_id)::int, sum(d.base)::numeric(19,2), sum(d.amount)::numeric(19,2)
            FROM tax.report_606(@c, @m) r
            JOIN pur.supplier_invoice si ON si.si_id = r.si_id
            JOIN tax.tax_determination_line d ON d.determination_id = si.tax_determination_id AND d.effect = 'WITHHOLDING'
            JOIN tax.fiscal_rule_version v ON v.rule_version_id = d.rule_version_id
            WHERE r.itbis_withheld > 0 OR r.isr_withheld > 0
            GROUP BY 1, 2 ORDER BY 1, 2
            """,
            r => new Ir17Line(r.GetString(0), r.NullableString(1), r.GetInt32(2), r.GetDecimal(3), r.GetDecimal(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("m", month)).ConfigureAwait(false);
        return ApiJson.Serialize(new Ir17Summary(
            query.Period, lines, lines.Where(l => l.Tax == "ITBIS").Sum(l => l.Amount), lines.Where(l => l.Tax == "ISR").Sum(l => l.Amount)));
    }
}
