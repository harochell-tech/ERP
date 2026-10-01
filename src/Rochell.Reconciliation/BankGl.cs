using System.Data.Common;
using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Reconciliation;

/// <summary>One in-transit item of a bank account at the cutoff (E-VS2-06-3, E-VS2-06-10), with its effect on the equation.</summary>
public sealed record BankItem(string Kind, string Reference, DateOnly Date, decimal Amount);

/// <summary>A bank account's BANK-GL at the cutoff: GL, statement, items and what is left unexplained.</summary>
public sealed record BankAccountReconciliation(
    Guid BankAccountId,
    DateOnly Cutoff,
    decimal GlBalance,
    decimal? StatementBalance,
    IReadOnlyList<BankItem> GlItems,
    IReadOnlyList<BankItem> LineItems,
    decimal? Difference);

/// <summary>
/// BANK-GL (VS#2 §8, E-VS2-06-2…4, E-VS2-06-8, E-VS2-06-10). Per bank account, at cutoff D (a CLOSED account at its closing date
/// if earlier):
/// <list type="bullet">
/// <item>GL = Σ debit − credit of its BANK entries posted on or before D.</item>
/// <item>Statement = closing balance of the latest imported statement covering D, minus the account's lines dated after D within
/// it (credit − debit). No covering statement while the account has GL movement: STATEMENT_MISSING.</item>
/// <item>GL items: each BANK entry ≤ D whose statement line is not matched on or before D — R-09 ↔ the payment's DEBIT line, the
/// R-09 reversal ↔ its CREDIT return line, R-10 ↔ its charge line; E-VS3-07-10: a transfer receipt's P-23 ↔ its CREDIT line, a
/// deposit's P-29 ↔ its CREDIT line, a bounce's P-24 ↔ its DEBIT line. A payment (or transfer receipt) and its reversal both in
/// transit cancel out and are not listed.</item>
/// <item>Line items: each line dated ≤ D without its entry posted ≤ D (unmatched lines, or matched ones posted later).</item>
/// <item>A BANK entry of no payment or charge is never an item: it shows as a difference.</item>
/// </list>
/// GL = statement + Σ GL items (debit − credit) − Σ line items (credit − debit); any difference is an ERROR, items older than 30 days
/// a WARNING. OPENING_DIFFERENCE: the first statement's opening balance differs from the GL the day before it (E-VS2-06-4).
/// </summary>
public static class BankGl
{
    /// <summary>E-VS2-06-3: an in-transit item older than this is reported as a warning.</summary>
    public const int AgedAfterDays = 30;

    public const string Component = "BANK-REC";

    private sealed record Account(Guid Id, string Status, DateOnly? ClosedOn);

    private sealed record Statement(DateOnly From, DateOnly To, decimal Opening, decimal Closing);

    private sealed record Entry(Guid EntryId, DateOnly PostingDate, decimal Signed, string Kind, string Reference, DateOnly? LineDate, Guid? PaymentId);

    private sealed record Line(Guid LineId, DateOnly ValueDate, decimal Signed, string Status, DateOnly? EntryDate, string Reference);

    public static Task<(IReadOnlyList<ReconFinding> Findings, IReadOnlyList<BankAccountReconciliation> Accounts)> RunAsync(
        CommandContext context, DateOnly cutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(new Db(context.Connection, context.Transaction, context.CompanyId), cutoff, null, cancellationToken);
    }

    /// <summary>Read-only (E-VS2-07-5): the same equation for one account, or all, without storing a run.</summary>
    public static Task<(IReadOnlyList<ReconFinding> Findings, IReadOnlyList<BankAccountReconciliation> Accounts)> ReadAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId, DateOnly cutoff, Guid? bankAccountId, CancellationToken cancellationToken)
        => RunAsync(new Db(connection, transaction, companyId), cutoff, bankAccountId, cancellationToken);

    private sealed record Db(DbConnection Connection, DbTransaction Transaction, Guid CompanyId);

    private static async Task<(IReadOnlyList<ReconFinding> Findings, IReadOnlyList<BankAccountReconciliation> Accounts)> RunAsync(
        Db context, DateOnly cutoff, Guid? only, CancellationToken cancellationToken)
    {
        var accounts = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT b.bank_account_id, b.status,
                   (SELECT min(e.business_date) FROM core.state_history h JOIN core.domain_event e ON e.company_id = h.company_id AND e.event_id = h.event_id
                    WHERE h.company_id = b.company_id AND h.aggregate_id = b.bank_account_id AND h.to_state = 'CLOSED')
            FROM fin.bank_account b WHERE b.company_id = @c AND (CAST(@only AS uuid) IS NULL OR b.bank_account_id = CAST(@only AS uuid)) ORDER BY b.bank_account_id
            """,
            r => new Account(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.Date(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("only", only)).ConfigureAwait(false);

        var findings = new List<ReconFinding>();
        var results = new List<BankAccountReconciliation>();
        foreach (var account in accounts)
        {
            var d = account.ClosedOn is { } closed && closed < cutoff ? closed : cutoff;
            var key = account.Id.ToString();
            var entries = await EntriesAsync(context, account.Id, d, cancellationToken).ConfigureAwait(false);
            var lines = await LinesAsync(context, account.Id, d, cancellationToken).ConfigureAwait(false);
            var gl = entries.Sum(e => e.Signed);
            if (entries.Count == 0 && lines.Count == 0 && gl == 0m)
            {
                continue; // E-VS2-06-8: no movement and zero balance up to D.
            }

            var statements = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                "SELECT period_from, period_to, opening_balance, closing_balance FROM fin.bank_statement WHERE company_id = @c AND bank_account_id = @b ORDER BY imported_at DESC, statement_id DESC",
                r => new Statement(r.Date(0), r.Date(1), r.GetDecimal(2), r.GetDecimal(3)),
                cancellationToken,
                ("c", context.CompanyId),
                ("b", account.Id)).ConfigureAwait(false);

            if (statements.Count > 0)
            {
                var first = statements.MinBy(s => s.From)!;
                var glBefore = await GlBeforeAsync(context, account.Id, first.From, cancellationToken).ConfigureAwait(false);
                if (glBefore != first.Opening)
                {
                    findings.Add(new ReconFinding($"{key}:opening:{Day(first.From)}", first.Opening, glBefore, "OPENING_DIFFERENCE", "ERROR", Component));
                }
            }

            var covering = statements.FirstOrDefault(s => s.From <= d && d <= s.To);
            if (covering is null)
            {
                findings.Add(new ReconFinding($"{key}:{Day(d)}", gl, null, "STATEMENT_MISSING", "ERROR", Component));
                results.Add(new BankAccountReconciliation(account.Id, d, gl, null, [], [], null));
                continue;
            }

            var after = await ReconSql.ScalarAsync<decimal>(
                context.Connection,
                context.Transaction,
                """
                SELECT coalesce(sum(CASE WHEN direction = 'CREDIT' THEN amount ELSE -amount END), 0) FROM fin.bank_statement_line
                WHERE company_id = @c AND bank_account_id = @b AND value_date > @d AND value_date <= @to
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("b", account.Id),
                ("d", d),
                ("to", covering.To)).ConfigureAwait(false);
            var statement = covering.Closing - after;

            // GL items: entries whose line is not matched on or before D; a payment's R-09 and its reversal both in transit cancel, and
            // so do a transfer receipt's P-23 and its reversal (E-VS3-07-10).
            var glItems = entries.Where(e => e.Kind != "UNLINKED_ENTRY" && (e.LineDate is null || e.LineDate > d)).ToList();
            var cancelled = glItems.Where(e => e.PaymentId is not null)
                .GroupBy(e => e.PaymentId)
                .Where(g => ((g.Any(e => e.Kind == "OUTSTANDING_PAYMENT") && g.Any(e => e.Kind == "OUTSTANDING_RETURN"))
                             || (g.Any(e => e.Kind == "OUTSTANDING_RECEIPT") && g.Any(e => e.Kind == "OUTSTANDING_RECEIPT_REVERSAL")))
                            && g.Sum(e => e.Signed) == 0m)
                .SelectMany(g => g)
                .ToHashSet();
            var glList = glItems.Where(e => !cancelled.Contains(e)).Select(e => new BankItem(e.Kind, e.Reference, e.PostingDate, e.Signed)).ToList();
            var lineList = lines.Where(l => l.EntryDate is null || l.EntryDate > d)
                .Select(l => new BankItem(l.Signed < 0m ? "UNRECORDED_DEBIT" : "UNRECORDED_CREDIT", l.Reference, l.ValueDate, l.Signed))
                .ToList();

            var difference = gl - (statement + glList.Sum(i => i.Amount) - lineList.Sum(i => i.Amount));
            if (difference != 0m)
            {
                findings.Add(new ReconFinding($"{key}:{Day(d)}", gl, statement + glList.Sum(i => i.Amount) - lineList.Sum(i => i.Amount), "BANK_GL_DIFFERENCE", "ERROR", Component));
            }

            foreach (var item in glList.Concat(lineList).Where(i => d.DayNumber - i.Date.DayNumber > AgedAfterDays))
            {
                findings.Add(new ReconFinding($"{key}:{item.Kind}:{item.Reference}", item.Amount, d.DayNumber - item.Date.DayNumber, "IN_TRANSIT_AGED", "WARNING", Component));
            }

            results.Add(new BankAccountReconciliation(account.Id, d, gl, statement, glList, lineList, difference));
        }

        return (findings, results);
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<decimal> GlBeforeAsync(Db context, Guid account, DateOnly from, CancellationToken cancellationToken)
        => await ReconSql.ScalarAsync<decimal>(
            context.Connection,
            context.Transaction,
            "SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND subledger_type = 'BANK' AND subledger_ref = @b AND posting_date < @from",
            cancellationToken,
            ("c", context.CompanyId),
            ("b", account),
            ("from", from)).ConfigureAwait(false);

    /// <summary>The account's BANK entries posted on or before D, each with the value date of its matched statement line, if any.</summary>
    private static async Task<List<Entry>> EntriesAsync(Db context, Guid account, DateOnly d, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT e.gl_entry_id, e.posting_date, e.debit - e.credit,
                   CASE WHEN pay.payment_id IS NOT NULL THEN 'OUTSTANDING_PAYMENT'
                        WHEN rev.payment_id IS NOT NULL THEN 'OUTSTANDING_RETURN'
                        WHEN chg.line_id IS NOT NULL THEN 'OUTSTANDING_CHARGE'
                        WHEN rc.receipt_id IS NOT NULL THEN 'OUTSTANDING_RECEIPT'
                        WHEN rr.receipt_id IS NOT NULL THEN 'OUTSTANDING_RECEIPT_REVERSAL'
                        WHEN rb.receipt_id IS NOT NULL THEN 'OUTSTANDING_BOUNCE'
                        WHEN dp.deposit_id IS NOT NULL THEN 'OUTSTANDING_DEPOSIT'
                        WHEN rf.refund_id IS NOT NULL THEN 'OUTSTANDING_REFUND'
                        ELSE 'UNLINKED_ENTRY' END,
                   coalesce(pay.payment_no, rev.payment_no, chg.line_id::text, rc.receipt_no, rr.receipt_no, rb.receipt_no, dp.deposit_no, rf.refund_no, e.gl_entry_id::text),
                   coalesce(dl.value_date, cl.value_date, chg.value_date, rcl.value_date, rbl.value_date, dpl.value_date, rfl.value_date),
                   coalesce(pay.payment_id, rev.payment_id, rc.receipt_id, rr.receipt_id)
            FROM fin.gl_entry e
            JOIN fin.gl_journal j ON j.journal_id = e.journal_id
            LEFT JOIN fin.gl_journal o ON o.journal_id = j.reverses_journal_id
            LEFT JOIN fin.payment pay ON j.journal_type = 'AUTO' AND pay.company_id = e.company_id AND pay.posting_event_id = j.source_event_id
            LEFT JOIN fin.payment rev ON j.journal_type = 'REVERSAL' AND rev.company_id = e.company_id AND rev.posting_event_id = o.source_event_id
            LEFT JOIN fin.bank_statement_line dl ON dl.matched_payment_id = pay.payment_id AND dl.direction = 'DEBIT' AND dl.status = 'MATCHED'
            LEFT JOIN fin.bank_statement_line cl ON cl.matched_payment_id = rev.payment_id AND cl.direction = 'CREDIT' AND cl.status = 'MATCHED'
            LEFT JOIN fin.bank_statement_line chg ON chg.company_id = e.company_id AND chg.status = 'CHARGE_RECOGNIZED' AND chg.charge_event_id = j.source_event_id
            LEFT JOIN fin.receipt rc ON j.journal_type = 'AUTO' AND rc.company_id = e.company_id AND rc.posting_event_id = j.source_event_id
            LEFT JOIN fin.receipt rr ON j.journal_type = 'REVERSAL' AND rr.company_id = e.company_id AND rr.posting_event_id = o.source_event_id
            LEFT JOIN fin.receipt rb ON j.journal_type = 'AUTO' AND rb.company_id = e.company_id AND rb.status = 'BOUNCED' AND rb.closing_event_id = j.source_event_id
            LEFT JOIN fin.receipt_deposit dp ON j.journal_type = 'AUTO' AND dp.company_id = e.company_id AND dp.posting_event_id = j.source_event_id
            LEFT JOIN fin.bank_statement_line rcl ON rcl.matched_receipt_id = rc.receipt_id AND rcl.direction = 'CREDIT'
            LEFT JOIN fin.bank_statement_line rbl ON rbl.matched_receipt_id = rb.receipt_id AND rbl.direction = 'DEBIT'
            LEFT JOIN fin.bank_statement_line dpl ON dpl.matched_deposit_id = dp.deposit_id
            LEFT JOIN fin.customer_refund rf ON j.journal_type = 'AUTO' AND rf.company_id = e.company_id AND rf.posting_event_id = j.source_event_id
            LEFT JOIN fin.bank_statement_line rfl ON rfl.matched_refund_id = rf.refund_id
            WHERE e.company_id = @c AND e.subledger_type = 'BANK' AND e.subledger_ref = @b AND e.posting_date <= @d
            ORDER BY e.posting_date, e.gl_entry_id
            """,
            r => new Entry(r.GetGuid(0), r.Date(1), r.GetDecimal(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.Date(5), r.NullableGuid(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("b", account),
            ("d", d)).ConfigureAwait(false);

    /// <summary>The account's statement lines dated on or before D, each with the posting date of its entry, if any.</summary>
    private static async Task<List<Line>> LinesAsync(Db context, Guid account, DateOnly d, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.value_date, CASE WHEN l.direction = 'CREDIT' THEN l.amount ELSE -l.amount END, l.status,
                   CASE WHEN l.matched_receipt_id IS NOT NULL THEN
                          (SELECT min(e.posting_date) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                           JOIN fin.receipt r ON j.source_event_id = CASE WHEN l.direction = 'CREDIT' THEN r.posting_event_id ELSE r.closing_event_id END
                           WHERE r.receipt_id = l.matched_receipt_id AND j.journal_type = 'AUTO' AND e.subledger_type = 'BANK')
                        WHEN l.matched_deposit_id IS NOT NULL THEN
                          (SELECT min(e.posting_date) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                           JOIN fin.receipt_deposit d ON d.posting_event_id = j.source_event_id
                           WHERE d.deposit_id = l.matched_deposit_id AND j.journal_type = 'AUTO' AND e.subledger_type = 'BANK')
                        WHEN l.matched_refund_id IS NOT NULL THEN
                          (SELECT min(e.posting_date) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                           JOIN fin.customer_refund f ON f.posting_event_id = j.source_event_id
                           WHERE f.refund_id = l.matched_refund_id AND j.journal_type = 'AUTO' AND e.subledger_type = 'BANK')
                        WHEN l.status = 'CHARGE_RECOGNIZED' THEN
                          (SELECT min(e.posting_date) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                           WHERE j.source_event_id = l.charge_event_id AND j.journal_type = 'AUTO' AND e.subledger_type = 'BANK')
                        WHEN l.status = 'MATCHED' AND l.direction = 'DEBIT' THEN
                          (SELECT min(e.posting_date) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                           JOIN fin.payment p ON p.posting_event_id = j.source_event_id
                           WHERE p.payment_id = l.matched_payment_id AND j.journal_type = 'AUTO' AND e.subledger_type = 'BANK')
                        WHEN l.status = 'MATCHED' AND l.direction = 'CREDIT' THEN
                          (SELECT min(e.posting_date) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                           JOIN fin.gl_journal o ON o.journal_id = j.reverses_journal_id
                           JOIN fin.payment p ON p.posting_event_id = o.source_event_id
                           WHERE p.payment_id = l.matched_payment_id AND j.journal_type = 'REVERSAL' AND e.subledger_type = 'BANK')
                   END,
                   coalesce(l.bank_reference, l.description)
            FROM fin.bank_statement_line l
            WHERE l.company_id = @c AND l.bank_account_id = @b AND l.value_date <= @d
            ORDER BY l.value_date, l.line_id
            """,
            r => new Line(r.GetGuid(0), r.Date(1), r.GetDecimal(2), r.GetString(3), r.IsDBNull(4) ? null : r.Date(4), r.GetString(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("b", account),
            ("d", d)).ConfigureAwait(false);
}

internal static class ReconSql
{
    public static async Task<T> ScalarAsync<T>(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(connection, transaction, sql, parameters);
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
