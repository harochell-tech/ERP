using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Treasury.Statements;

/// <summary>
/// E-VS3-10-8: what an UNMATCHED statement line can be matched to among the customer receipts, read with bank:read so the treasurer
/// needs no sales:read. A CREDIT line: RECORDED transfer receipts of the same account and amount dated within ±10 days, and deposit
/// slips of the same account and total dated in [deposit date, +10 days]. A DEBIT line: deposited cheques of that account and
/// amount (to mark bounced) and BOUNCED cheques whose return is not matched yet.
/// </summary>
public sealed record ListReceiptCandidates(Guid CompanyId, Guid SessionId, Guid LineId) : IQuery;

/// <summary><c>Kind</c>: TRANSFER, DEPOSIT, CHEQUE_TO_BOUNCE or BOUNCED_CHEQUE; <c>Version</c> is the receipt's or the deposit's.</summary>
public sealed record ReceiptCandidate(string Kind, Guid? ReceiptId, Guid? DepositId, string Number, string? CustomerName, DateOnly Date, string Amount, long Version);

public sealed record ReceiptCandidates(Guid LineId, long LineVersion, string Direction, DateOnly ValueDate, string Amount, IReadOnlyList<ReceiptCandidate> Candidates);

[RequiresPermission("bank:read")]
public sealed class ListReceiptCandidatesHandler : IQueryHandler<ListReceiptCandidates>
{
    public string QueryType => "Treasury.ListReceiptCandidates";

    private sealed record Line(Guid BankAccountId, long Version, string Direction, DateOnly ValueDate, decimal Amount, string Status);

    public async Task<string> HandleAsync(ListReceiptCandidates query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var line = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT bank_account_id, version, direction, value_date, amount, status FROM fin.bank_statement_line WHERE company_id = @c AND line_id = @l",
            r => new Line(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.Date(3), r.GetDecimal(4), r.GetString(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LineId)).ConfigureAwait(false)
            ?? throw new DomainException(StatementErrors.NotFound, "The statement line does not exist.");
        var candidates = line.Status != "UNMATCHED"
            ? []
            : await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT 'TRANSFER', r.receipt_id, NULL::uuid, r.receipt_no, p.legal_name, r.value_date, r.amount::numeric(19,2), r.version
                FROM fin.receipt r JOIN md.party p ON p.party_id = r.party_id
                WHERE @dir = 'CREDIT' AND r.company_id = @c AND r.method = 'TRANSFER' AND r.status = 'RECORDED' AND r.bank_status = 'DEPOSITED'
                  AND r.bank_account_id = @b AND r.amount = @a AND abs(@d - r.value_date) <= @window
                UNION ALL
                SELECT 'DEPOSIT', NULL, d.deposit_id, d.deposit_no, NULL, d.deposit_date, d.total::numeric(19,2), d.version
                FROM fin.receipt_deposit d
                WHERE @dir = 'CREDIT' AND d.company_id = @c AND d.status = 'DEPOSITED' AND d.bank_account_id = @b AND d.total = @a
                  AND @d BETWEEN d.deposit_date AND d.deposit_date + @window
                UNION ALL
                SELECT CASE WHEN r.status = 'BOUNCED' THEN 'BOUNCED_CHEQUE' ELSE 'CHEQUE_TO_BOUNCE' END, r.receipt_id, NULL, r.receipt_no, p.legal_name,
                       d.deposit_date, r.amount::numeric(19,2), r.version
                FROM fin.receipt r JOIN fin.receipt_deposit d ON d.deposit_id = r.deposit_id JOIN md.party p ON p.party_id = r.party_id
                WHERE @dir = 'DEBIT' AND r.company_id = @c AND r.method = 'CHEQUE' AND r.status IN ('RECORDED', 'BOUNCED') AND d.bank_account_id = @b
                  AND r.amount = @a AND @d >= d.deposit_date
                  AND NOT EXISTS (SELECT 1 FROM fin.bank_statement_line x WHERE x.matched_receipt_id = r.receipt_id AND x.direction = 'DEBIT')
                ORDER BY 1, 4
                """,
                r => new ReceiptCandidate(r.GetString(0), r.NullableGuid(1), r.NullableGuid(2), r.GetString(3), r.NullableString(4), r.Date(5), Payments.PaymentRules.Money(r.GetDecimal(6)), r.GetInt64(7)),
                cancellationToken,
                ("dir", line.Direction),
                ("c", context.CompanyId),
                ("b", line.BankAccountId),
                ("a", line.Amount),
                ("d", line.ValueDate),
                ("window", ReceiptLines.WindowDays)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReceiptCandidates(query.LineId, line.Version, line.Direction, line.ValueDate, Payments.PaymentRules.Money(line.Amount), candidates));
    }
}
