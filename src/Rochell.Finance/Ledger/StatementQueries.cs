using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Ledger;

// FIN1-03: trial balance, account ledger, balance sheet and income statement (ledger:read, E-FIN1-9) and the report structures
// behind the statements (configuration:read, like the other approval screens). Amounts are Σ(debit − credit) of fin.gl_entry.

internal static class Statements
{
    public static readonly string[] IncomeClasses = ["REVENUE", "COST", "EXPENSE"];

    /// <summary>0.00: adding it keeps two decimals in the JSON ("0.00"), where a bare 0m would print "0".</summary>
    public static readonly decimal Zero = new(0, 0, 0, false, 2);

    /// <summary>E-FIN1-03-3: the fiscal year is the calendar year.</summary>
    public static DateOnly YearStart(DateOnly date) => new(date.Year, 1, 1);

    /// <summary>The rows of a query that returns value tuples or scalars (Reading.SingleOrDefaultAsync takes reference types).</summary>
    public static Task<List<T>> RowsAsync<T>(QueryContext context, string sql, Func<System.Data.Common.DbDataReader, T> map, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => Reading.ListAsync(context.Connection, context.Transaction, sql, map, cancellationToken, parameters);

    public static void EnsureRange(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "from must not be after to.");
        }
    }

    /// <summary>E-FIN1-01-1, E-FIN1-03-8: no statement while an active account has no class.</summary>
    public static async Task EnsureClassifiedAsync(QueryContext context, CancellationToken cancellationToken)
    {
        var missing = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT code FROM fin.account WHERE company_id = @c AND status = 'ACTIVE' AND account_class IS NULL ORDER BY code",
            r => r.GetString(0),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new DomainException(LedgerErrors.AccountClassMissing, $"Active accounts without a class: {string.Join(", ", missing)}.");
        }
    }

    public sealed record AccountAmount(Guid AccountId, string Code, string Name, string AccountClass, decimal Balance);

    public sealed record StructureLine(Guid LineId, string LineCode, string Caption, string? ParentLineCode, int Sign, int OrderNo);

    public static async Task<(Guid Id, int Version, List<StructureLine> Lines, Dictionary<Guid, Guid> AccountLine)> ActiveStructureAsync(
        QueryContext context, string report, CancellationToken cancellationToken)
    {
        var headers = await RowsAsync(
            context,
            "SELECT structure_version_id, version FROM fin.report_structure_version WHERE company_id = @c AND report = @r AND status = 'ACTIVE'",
            r => (r.GetGuid(0), r.GetInt32(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", report)).ConfigureAwait(false);
        if (headers.Count == 0)
        {
            throw new DomainException(LedgerErrors.StructureMissing, $"There is no approved {report} structure (E-FIN1-5).");
        }

        var header = headers[0];

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT report_line_id, line_code, caption, parent_line_code, sign, order_no FROM fin.report_line WHERE structure_version_id = @s ORDER BY order_no, line_code",
            r => new StructureLine(r.GetGuid(0), r.GetString(1), r.GetString(2), r.NullableString(3), r.GetInt32(4), r.GetInt32(5)),
            cancellationToken,
            ("s", header.Item1)).ConfigureAwait(false);
        var accounts = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT account_id, report_line_id FROM fin.report_line_account WHERE structure_version_id = @s",
            r => (r.GetGuid(0), r.GetGuid(1)),
            cancellationToken,
            ("s", header.Item1)).ConfigureAwait(false);
        return (header.Item1, header.Item2, lines, accounts.ToDictionary(a => a.Item1, a => a.Item2));
    }

    /// <summary>
    /// E-FIN1-03-5: a line shows sign × Σ(debit − credit) of its accounts and of every line below it; lines come in tree order
    /// (children by order_no under their parent).
    /// </summary>
    public static (List<StatementLine> Lines, List<StatementAccount> Unassigned) Present(
        List<StructureLine> structure, Dictionary<Guid, Guid> accountLine, IReadOnlyList<AccountAmount> amounts)
    {
        var byLine = structure.ToDictionary(l => l.LineId);
        var byCode = structure.ToDictionary(l => l.LineCode, StringComparer.Ordinal);
        var raw = structure.ToDictionary(l => l.LineCode, _ => Zero, StringComparer.Ordinal);
        var own = structure.ToDictionary(l => l.LineCode, _ => new List<AccountAmount>(), StringComparer.Ordinal);
        var unassigned = new List<StatementAccount>();
        foreach (var a in amounts)
        {
            if (!accountLine.TryGetValue(a.AccountId, out var lineId))
            {
                if (a.Balance != 0m)
                {
                    unassigned.Add(new StatementAccount(a.AccountId, a.Code, a.Name, a.AccountClass, a.Balance));
                }

                continue;
            }

            var code = byLine[lineId].LineCode;
            own[code].Add(a);
            for (string? c = code; c is not null; c = byCode[c].ParentLineCode)
            {
                raw[c] += a.Balance;
            }
        }

        var result = new List<StatementLine>();
        void Walk(string? parent, int depth)
        {
            foreach (var line in structure.Where(l => l.ParentLineCode == parent).OrderBy(l => l.OrderNo).ThenBy(l => l.LineCode, StringComparer.Ordinal))
            {
                result.Add(new StatementLine(
                    line.LineCode,
                    line.Caption,
                    line.ParentLineCode,
                    depth,
                    line.Sign * raw[line.LineCode],
                    own[line.LineCode].OrderBy(a => a.Code, StringComparer.Ordinal).Select(a => new StatementAccount(a.AccountId, a.Code, a.Name, a.AccountClass, line.Sign * a.Balance)).ToList()));
                Walk(line.LineCode, depth + 1);
            }
        }

        Walk(null, 0);
        return (result, unassigned);
    }

    /// <summary>Σ(debit − credit) per classed account of <paramref name="classes"/> with posting date in [from, to].</summary>
    public static Task<List<AccountAmount>> AmountsAsync(QueryContext context, string[] classes, DateOnly? from, DateOnly to, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.account_id, a.code, a.name, a.account_class, coalesce(sum(e.debit - e.credit), 0)::numeric(19,2)
            FROM fin.account a
            LEFT JOIN fin.gl_entry e ON e.company_id = a.company_id AND e.account_id = a.account_id AND e.posting_date <= @to
              AND (CAST(@from AS date) IS NULL OR e.posting_date >= CAST(@from AS date))
            WHERE a.company_id = @c AND a.account_class = ANY (@classes)
            GROUP BY a.account_id, a.code, a.name, a.account_class
            ORDER BY a.code
            """,
            r => new AccountAmount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("classes", classes),
            ("from", from),
            ("to", to));

    public static async Task<decimal> IncomeResultAsync(QueryContext context, DateOnly? from, DateOnly to, bool before, CancellationToken cancellationToken)
        => (await RowsAsync(
            context,
            $"""
            SELECT coalesce(sum(e.credit - e.debit), 0)::numeric(19,2)
            FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id
            WHERE e.company_id = @c AND a.account_class = ANY (@classes)
              AND {(before ? "e.posting_date < @to" : "e.posting_date <= @to AND e.posting_date >= CAST(@from AS date)")}
            """,
            r => r.GetDecimal(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("classes", IncomeClasses),
            ("from", from),
            ("to", to)).ConfigureAwait(false)).Single();
}

public sealed record StatementAccount(Guid AccountId, string Code, string Name, string AccountClass, decimal Amount);

public sealed record StatementLine(string LineCode, string Caption, string? ParentLineCode, int Depth, decimal Amount, IReadOnlyList<StatementAccount> Accounts);

// ---------------------------------------------------------------------------------------------------------------------------
// Trial balance

public sealed record GetTrialBalance(
    Guid CompanyId, Guid SessionId, DateOnly From, DateOnly To, Guid? PlantId = null, Guid? PartyId = null, Guid? BankAccountId = null) : IQuery;

/// <summary>A row per account; the row without account is the result of prior years (E-FIN1-03-4).</summary>
public sealed record TrialBalanceRow(Guid? AccountId, string Code, string Name, string? AccountClass, decimal Opening, decimal Debit, decimal Credit, decimal Closing);

/// <summary>E-FIN1-03-6: <see cref="Balanced"/> (debits = credits and Σ closing = 0) is only required when not <see cref="Filtered"/>.</summary>
public sealed record TrialBalance(
    DateOnly From, DateOnly To, bool Filtered, IReadOnlyList<TrialBalanceRow> Rows, decimal TotalOpening, decimal TotalDebit, decimal TotalCredit, decimal TotalClosing, bool Balanced);

[RequiresPermission("ledger:read")]
public sealed class GetTrialBalanceHandler : IQueryHandler<GetTrialBalance>
{
    public const string PriorYearsCaption = "Resultados de ejercicios anteriores";

    public string QueryType => "Finance.GetTrialBalance";

    public async Task<string> HandleAsync(GetTrialBalance query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        Statements.EnsureRange(query.From, query.To);
        const string Filter = """
            e.company_id = @c AND e.posting_date <= @to
              AND (CAST(@plant AS uuid) IS NULL OR e.plant_id = CAST(@plant AS uuid))
              AND (CAST(@party AS uuid) IS NULL OR e.party_id = CAST(@party AS uuid))
              AND (CAST(@bank AS uuid) IS NULL OR (e.subledger_type = 'BANK' AND e.subledger_ref = CAST(@bank AS uuid)))
            """;
        (string Name, object? Value)[] parameters =
        [
            ("c", context.CompanyId), ("from", query.From), ("to", query.To), ("year", Statements.YearStart(query.From)),
            ("plant", query.PlantId), ("party", query.PartyId), ("bank", query.BankAccountId), ("income", Statements.IncomeClasses),
        ];
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT a.account_id, a.code, a.name, a.account_class,
                   sum(CASE WHEN e.posting_date < @from AND (a.account_class IS NULL OR NOT (a.account_class = ANY (@income)) OR e.posting_date >= @year)
                            THEN e.debit - e.credit ELSE 0 END)::numeric(19,2),
                   sum(CASE WHEN e.posting_date >= @from THEN e.debit ELSE 0 END)::numeric(19,2),
                   sum(CASE WHEN e.posting_date >= @from THEN e.credit ELSE 0 END)::numeric(19,2)
            FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id
            WHERE {Filter}
            GROUP BY a.account_id, a.code, a.name, a.account_class
            ORDER BY a.code
            """,
            r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.NullableString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6)),
            cancellationToken,
            parameters).ConfigureAwait(false);
        var prior = (await Statements.RowsAsync(
            context,
            $"""
            SELECT coalesce(sum(e.debit - e.credit), 0)::numeric(19,2)
            FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id
            WHERE {Filter} AND e.posting_date < @year AND a.account_class = ANY (@income)
            """,
            r => r.GetDecimal(0),
            cancellationToken,
            parameters).ConfigureAwait(false)).Single();

        var result = rows
            .Where(r => r.Item5 != 0m || r.Item6 != 0m || r.Item7 != 0m)
            .Select(r => new TrialBalanceRow(r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6, r.Item7, r.Item5 + r.Item6 - r.Item7))
            .ToList();
        if (prior != 0m)
        {
            result.Add(new TrialBalanceRow(null, string.Empty, PriorYearsCaption, "EQUITY", prior, Statements.Zero, Statements.Zero, prior));
        }

        var filtered = query.PlantId is not null || query.PartyId is not null || query.BankAccountId is not null;
        var (opening, debit, credit, closing) = (Statements.Zero + result.Sum(r => r.Opening), Statements.Zero + result.Sum(r => r.Debit), Statements.Zero + result.Sum(r => r.Credit), Statements.Zero + result.Sum(r => r.Closing));
        return ApiJson.Serialize(new TrialBalance(query.From, query.To, filtered, result, opening, debit, credit, closing, debit == credit && closing == 0m && opening == 0m));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Account ledger

public sealed record GetAccountLedger(Guid CompanyId, Guid SessionId, Guid AccountId, DateOnly From, DateOnly To, int Limit = 100, int Offset = 0, bool All = false) : IQuery;

public sealed record LedgerMovement(
    Guid GlEntryId, DateOnly PostingDate, string JournalType, string? EventType, string? DocumentKind, string? DocumentNumber, string RuleLineCode,
    decimal Debit, decimal Credit, decimal Balance, Guid? PlantId, Guid? PartyId);

public sealed record AccountLedger(
    Guid AccountId, string Code, string Name, string? AccountClass, DateOnly From, DateOnly To, decimal Opening, decimal TotalDebit, decimal TotalCredit, decimal Closing,
    int Count, IReadOnlyList<LedgerMovement> Movements, int Limit, int Offset);

[RequiresPermission("ledger:read")]
public sealed class GetAccountLedgerHandler : IQueryHandler<GetAccountLedger>
{
    public string QueryType => "Finance.GetAccountLedger";

    public async Task<string> HandleAsync(GetAccountLedger query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        Statements.EnsureRange(query.From, query.To);
        if (!query.All)
        {
            QueryErrors.EnsurePaging(query.Limit, query.Offset);
        }

        var account = (await Statements.RowsAsync(
            context,
            "SELECT code, name, account_class FROM fin.account WHERE company_id = @c AND account_id = @a",
            r => ((string, string, string?)?)(r.GetString(0), r.GetString(1), r.NullableString(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("a", query.AccountId)).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new DomainException(QueryErrors.NotFound, "The account does not exist.");

        // E-FIN1-03-4: income accounts open at the start of the fiscal year of "from".
        var openFrom = account.Item3 is not null && Statements.IncomeClasses.Contains(account.Item3) ? Statements.YearStart(query.From) : (DateOnly?)null;
        var totals = (await Statements.RowsAsync(
            context,
            """
            SELECT coalesce(sum(CASE WHEN posting_date < @from AND (CAST(@open AS date) IS NULL OR posting_date >= CAST(@open AS date)) THEN debit - credit ELSE 0 END), 0)::numeric(19,2),
                   coalesce(sum(CASE WHEN posting_date >= @from THEN debit ELSE 0 END), 0)::numeric(19,2),
                   coalesce(sum(CASE WHEN posting_date >= @from THEN credit ELSE 0 END), 0)::numeric(19,2),
                   count(*) FILTER (WHERE posting_date >= @from)::int
            FROM fin.gl_entry WHERE company_id = @c AND account_id = @a AND posting_date <= @to
            """,
            r => (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetInt32(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("a", query.AccountId),
            ("from", query.From),
            ("to", query.To),
            ("open", openFrom)).ConfigureAwait(false)).Single();
        var movements = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.* FROM (
              SELECT e.gl_entry_id, e.posting_date, j.journal_type, ev.event_type, d.kind, d.number, e.rule_line_code,
                     e.debit::numeric(19,2), e.credit::numeric(19,2),
                     (@opening + sum(e.debit - e.credit) OVER (ORDER BY e.posting_date, j.occurred_at, e.journal_id, e.line_no))::numeric(19,2),
                     e.plant_id, e.party_id,
                     row_number() OVER (ORDER BY e.posting_date, j.occurred_at, e.journal_id, e.line_no) AS n
              FROM fin.gl_entry e
              JOIN fin.gl_journal j ON j.journal_id = e.journal_id
              LEFT JOIN core.domain_event ev ON ev.company_id = e.company_id AND ev.event_id = e.source_event_id
              LEFT JOIN LATERAL (
                SELECT CASE ev.aggregate_type
                         WHEN 'GoodsReceipt' THEN 'GOODS_RECEIPT' WHEN 'GoodsReceiptReversal' THEN 'GOODS_RECEIPT_REVERSAL'
                         WHEN 'ReceiptCorrection' THEN 'RECEIPT_CORRECTION' WHEN 'SupplierInvoice' THEN 'SUPPLIER_INVOICE'
                         WHEN 'Payment' THEN 'PAYMENT' WHEN 'ManualJournal' THEN 'MANUAL_JOURNAL' WHEN 'BankStatementLine' THEN 'BANK_CHARGE' END AS kind,
                       CASE ev.aggregate_type
                         WHEN 'GoodsReceipt' THEN (SELECT g.gr_no FROM pur.goods_receipt g WHERE g.gr_id = ev.aggregate_id)
                         WHEN 'GoodsReceiptReversal' THEN (SELECT g.gr_no FROM pur.goods_receipt_reversal r JOIN pur.goods_receipt g ON g.gr_id = r.reversed_gr_id WHERE r.grr_id = ev.aggregate_id)
                         WHEN 'ReceiptCorrection' THEN (SELECT g.gr_no FROM pur.receipt_correction rc JOIN pur.goods_receipt g ON g.gr_id = rc.gr_id WHERE rc.rc_id = ev.aggregate_id)
                         WHEN 'SupplierInvoice' THEN (SELECT si.supplier_fiscal_number FROM pur.supplier_invoice si WHERE si.si_id = ev.aggregate_id)
                         WHEN 'Payment' THEN (SELECT p.payment_no FROM fin.payment p WHERE p.payment_id = ev.aggregate_id)
                         WHEN 'ManualJournal' THEN (SELECT mj.journal_no FROM fin.manual_journal mj WHERE mj.manual_journal_id = ev.aggregate_id)
                         WHEN 'BankStatementLine' THEN (SELECT l.description FROM fin.bank_statement_line l WHERE l.line_id = ev.aggregate_id) END AS number
              ) d ON true
              WHERE e.company_id = @c AND e.account_id = @a AND e.posting_date BETWEEN @from AND @to) m
            WHERE CAST(@all AS boolean) OR (m.n > @offset AND m.n <= @offset + @limit)
            ORDER BY m.n
            """,
            r => new LedgerMovement(
                r.GetGuid(0), r.Date(1), r.GetString(2), r.NullableString(3), r.NullableString(4), r.NullableString(5), r.GetString(6),
                r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.NullableGuid(10), r.NullableGuid(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("a", query.AccountId),
            ("from", query.From),
            ("to", query.To),
            ("opening", totals.Item1),
            ("all", query.All),
            ("offset", query.Offset),
            ("limit", query.Limit)).ConfigureAwait(false);
        var (opening, debit, credit, count) = totals;
        return ApiJson.Serialize(new AccountLedger(
            query.AccountId, account.Item1, account.Item2, account.Item3, query.From, query.To, opening, debit, credit, opening + debit - credit, count, movements, query.Limit, query.Offset));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Balance sheet and income statement

public sealed record GetBalanceSheet(Guid CompanyId, Guid SessionId, DateOnly AsOf) : IQuery;

/// <summary>
/// E-FIN1-03-3/5: assets = liabilities + equity + result of the year + result of prior years; <see cref="Difference"/> is 0.00
/// whenever the ledger balances. Accounts with a balance that the structure does not place are listed apart and still count.
/// </summary>
public sealed record BalanceSheet(
    DateOnly AsOf, int StructureVersion, IReadOnlyList<StatementLine> Lines, IReadOnlyList<StatementAccount> UnassignedAccounts,
    decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity, decimal CurrentYearResult, decimal PriorYearsResult, decimal Difference, bool Balanced);

[RequiresPermission("ledger:read")]
public sealed class GetBalanceSheetHandler : IQueryHandler<GetBalanceSheet>
{
    public string QueryType => "Finance.GetBalanceSheet";

    public async Task<string> HandleAsync(GetBalanceSheet query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        await Statements.EnsureClassifiedAsync(context, cancellationToken).ConfigureAwait(false);
        var structure = await Statements.ActiveStructureAsync(context, "BALANCE_SHEET", cancellationToken).ConfigureAwait(false);
        var amounts = await Statements.AmountsAsync(context, ["ASSET", "LIABILITY", "EQUITY"], null, query.AsOf, cancellationToken).ConfigureAwait(false);
        var (lines, unassigned) = Statements.Present(structure.Lines, structure.AccountLine, amounts);
        var yearStart = Statements.YearStart(query.AsOf);
        var current = await Statements.IncomeResultAsync(context, yearStart, query.AsOf, before: false, cancellationToken).ConfigureAwait(false);
        var prior = await Statements.IncomeResultAsync(context, null, yearStart, before: true, cancellationToken).ConfigureAwait(false);
        var assets = Statements.Zero + amounts.Where(a => a.AccountClass == "ASSET").Sum(a => a.Balance);
        var liabilities = Statements.Zero - amounts.Where(a => a.AccountClass == "LIABILITY").Sum(a => a.Balance);
        var equity = Statements.Zero - amounts.Where(a => a.AccountClass == "EQUITY").Sum(a => a.Balance);
        var difference = assets - liabilities - equity - current - prior;
        return ApiJson.Serialize(new BalanceSheet(query.AsOf, structure.Version, lines, unassigned, assets, liabilities, equity, current, prior, difference, difference == 0m));
    }
}

public sealed record GetIncomeStatement(Guid CompanyId, Guid SessionId, DateOnly From, DateOnly To) : IQuery;

/// <summary>Net income = revenue − cost − expenses of [from, to].</summary>
public sealed record IncomeStatement(
    DateOnly From, DateOnly To, int StructureVersion, IReadOnlyList<StatementLine> Lines, IReadOnlyList<StatementAccount> UnassignedAccounts,
    decimal Revenue, decimal Cost, decimal Expenses, decimal NetIncome);

[RequiresPermission("ledger:read")]
public sealed class GetIncomeStatementHandler : IQueryHandler<GetIncomeStatement>
{
    public string QueryType => "Finance.GetIncomeStatement";

    public async Task<string> HandleAsync(GetIncomeStatement query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        Statements.EnsureRange(query.From, query.To);
        await Statements.EnsureClassifiedAsync(context, cancellationToken).ConfigureAwait(false);
        var structure = await Statements.ActiveStructureAsync(context, "INCOME_STATEMENT", cancellationToken).ConfigureAwait(false);
        var amounts = await Statements.AmountsAsync(context, Statements.IncomeClasses, query.From, query.To, cancellationToken).ConfigureAwait(false);
        var (lines, unassigned) = Statements.Present(structure.Lines, structure.AccountLine, amounts);
        var revenue = Statements.Zero - amounts.Where(a => a.AccountClass == "REVENUE").Sum(a => a.Balance);
        var cost = Statements.Zero + amounts.Where(a => a.AccountClass == "COST").Sum(a => a.Balance);
        var expenses = Statements.Zero + amounts.Where(a => a.AccountClass == "EXPENSE").Sum(a => a.Balance);
        return ApiJson.Serialize(new IncomeStatement(query.From, query.To, structure.Version, lines, unassigned, revenue, cost, expenses, revenue - cost - expenses));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Report structures

public sealed record ListReportStructures(Guid CompanyId, Guid SessionId, string? Report = null) : IQuery;

public sealed record ReportStructureSummary(
    Guid StructureVersionId, string Report, int Version, DateOnly EffectiveFrom, string Status, string? PreparedBy, Guid PreparedById, string? ApprovedBy, int Lines, int Accounts);

public sealed record ReportStructureList(IReadOnlyList<ReportStructureSummary> Items);

[RequiresPermission("configuration:read")]
public sealed class ListReportStructuresHandler : IQueryHandler<ListReportStructures>
{
    public string QueryType => "Finance.ListReportStructures";

    public async Task<string> HandleAsync(ListReportStructures query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.structure_version_id, v.report, v.version, v.effective_from, v.status, coalesce(pu.display_name, pu.email), v.prepared_by, coalesce(au.display_name, au.email),
                   (SELECT count(*) FROM fin.report_line l WHERE l.structure_version_id = v.structure_version_id)::int,
                   (SELECT count(*) FROM fin.report_line_account x WHERE x.structure_version_id = v.structure_version_id)::int
            FROM fin.report_structure_version v
            JOIN iam.user pu ON pu.user_id = v.prepared_by
            LEFT JOIN iam.user au ON au.user_id = v.approved_by
            WHERE v.company_id = @c AND (CAST(@r AS text) IS NULL OR v.report = CAST(@r AS text))
            ORDER BY v.report, v.version DESC
            """,
            r => new ReportStructureSummary(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.Date(3), r.GetString(4), r.NullableString(5), r.GetGuid(6), r.NullableString(7), r.GetInt32(8), r.GetInt32(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", query.Report)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReportStructureList(items));
    }
}

public sealed record GetReportStructure(Guid CompanyId, Guid SessionId, Guid StructureVersionId) : IQuery;

public sealed record ReportStructureAccount(Guid AccountId, string Code, string Name, string? AccountClass);

public sealed record ReportStructureLineView(string LineCode, string Caption, string? ParentLineCode, int Sign, int OrderNo, IReadOnlyList<ReportStructureAccount> Accounts);

/// <summary>A structure with its lines and, for a DRAFT, the active accounts of its classes that no line holds yet.</summary>
public sealed record ReportStructureDetail(ReportStructureSummary Header, IReadOnlyList<ReportStructureLineView> Lines, IReadOnlyList<ReportStructureAccount> MissingAccounts);

[RequiresPermission("configuration:read")]
public sealed class GetReportStructureHandler : IQueryHandler<GetReportStructure>
{
    public string QueryType => "Finance.GetReportStructure";

    public async Task<string> HandleAsync(GetReportStructure query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var list = System.Text.Json.JsonSerializer.Deserialize<ReportStructureList>(
            await new ListReportStructuresHandler().HandleAsync(new ListReportStructures(query.CompanyId, query.SessionId), context, cancellationToken).ConfigureAwait(false),
            ApiJson.Options)!;
        var header = list.Items.SingleOrDefault(i => i.StructureVersionId == query.StructureVersionId)
            ?? throw new DomainException(QueryErrors.NotFound, "The report structure does not exist.");
        var accounts = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_code, a.account_id, a.code, a.name, a.account_class
            FROM fin.report_line_account x JOIN fin.report_line l ON l.report_line_id = x.report_line_id JOIN fin.account a ON a.account_id = x.account_id
            WHERE x.structure_version_id = @s ORDER BY a.code
            """,
            r => (r.GetString(0), new ReportStructureAccount(r.GetGuid(1), r.GetString(2), r.GetString(3), r.NullableString(4))),
            cancellationToken,
            ("s", query.StructureVersionId)).ConfigureAwait(false);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT line_code, caption, parent_line_code, sign, order_no FROM fin.report_line WHERE structure_version_id = @s ORDER BY order_no, line_code",
            r => (r.GetString(0), r.GetString(1), r.NullableString(2), r.GetInt32(3), r.GetInt32(4)),
            cancellationToken,
            ("s", query.StructureVersionId)).ConfigureAwait(false);
        var missing = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.account_id, a.code, a.name, a.account_class FROM fin.account a
            WHERE a.company_id = @c AND a.status = 'ACTIVE' AND a.account_class = ANY (@classes)
              AND NOT EXISTS (SELECT 1 FROM fin.report_line_account x WHERE x.structure_version_id = @s AND x.account_id = a.account_id)
            ORDER BY a.code
            """,
            r => new ReportStructureAccount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.NullableString(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("classes", ReportStructures.ClassesOf(header.Report).ToArray()),
            ("s", query.StructureVersionId)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReportStructureDetail(
            header,
            lines.Select(l => new ReportStructureLineView(l.Item1, l.Item2, l.Item3, l.Item4, l.Item5, accounts.Where(a => a.Item1 == l.Item1).Select(a => a.Item2).ToList())).ToList(),
            missing));
    }
}
