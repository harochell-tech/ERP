using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Treasury.Statements;

/// <summary>
/// E-VS2-05-6: for each UNMATCHED DEBIT line of a statement, the RELEASED payments of the same bank account with the exact amount
/// and a value date in [line date − 10 days, line date] that the line names — its reference or description contains the payment
/// number, or its reference equals the payment's — or, when none does, the only such payment. A suggestion is never applied by
/// itself: MatchBankLine needs a person.
/// </summary>
public sealed record SuggestBankMatches(Guid CompanyId, Guid SessionId, Guid StatementId) : IQuery;

public sealed record MatchCandidate(Guid PaymentId, string PaymentNo, long PaymentVersion, DateOnly ValueDate, string? BankReference, string Basis);

public sealed record LineSuggestion(Guid LineId, long LineVersion, DateOnly ValueDate, string Amount, string? BankReference, string Description, IReadOnlyList<MatchCandidate> Candidates);

public sealed record MatchSuggestions(Guid StatementId, IReadOnlyList<LineSuggestion> Lines);

[RequiresPermission("bank:read")]
public sealed class SuggestBankMatchesHandler : IQueryHandler<SuggestBankMatches>
{
    public const string ByPaymentNo = "PAYMENT_NO";
    public const string ByReference = "REFERENCE";
    public const string ByAmountOnly = "AMOUNT_ONLY";

    public string QueryType => "Treasury.SuggestBankMatches";

    private sealed record Row(
        Guid LineId, long LineVersion, DateOnly ValueDate, decimal Amount, string? LineReference, string Description,
        Guid? PaymentId, string? PaymentNo, long? PaymentVersion, DateOnly? PaymentValueDate, string? PaymentReference, string? Basis);

    public async Task<string> HandleAsync(SuggestBankMatches query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var exists = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT statement_id FROM fin.bank_statement WHERE statement_id = @s AND company_id = @c",
            r => r.GetGuid(0).ToString(),
            cancellationToken,
            ("s", query.StatementId),
            ("c", context.CompanyId)).ConfigureAwait(false);
        if (exists is null)
        {
            throw new DomainException(StatementErrors.NotFound, "The statement does not exist.");
        }

        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            WITH candidate AS (
              SELECT l.line_id, p.payment_id, p.payment_no, p.version, p.value_date, p.bank_reference,
                     CASE WHEN strpos(upper(coalesce(l.bank_reference, '') || ' ' || l.description), p.payment_no) > 0 THEN 'PAYMENT_NO'
                          WHEN l.bank_reference IS NOT NULL AND upper(l.bank_reference) = upper(p.bank_reference) THEN 'REFERENCE' END AS basis
              FROM fin.bank_statement_line l
              JOIN fin.payment p ON p.company_id = l.company_id AND p.bank_account_id = l.bank_account_id AND p.status::text = 'RELEASED'
                AND p.amount = l.amount AND l.value_date BETWEEN p.value_date AND p.value_date + @window
              WHERE l.company_id = @c AND l.statement_id = @s AND l.status = 'UNMATCHED' AND l.direction = 'DEBIT'
            ), chosen AS (
              SELECT c.line_id, c.payment_id, c.payment_no, c.version, c.value_date, c.bank_reference, c.basis FROM candidate c WHERE c.basis IS NOT NULL
              UNION ALL
              SELECT c.line_id, c.payment_id, c.payment_no, c.version, c.value_date, c.bank_reference, 'AMOUNT_ONLY' FROM candidate c
              WHERE NOT EXISTS (SELECT 1 FROM candidate n WHERE n.line_id = c.line_id AND n.basis IS NOT NULL)
                AND (SELECT count(*) FROM candidate o WHERE o.line_id = c.line_id) = 1
            )
            SELECT l.line_id, l.version, l.value_date, l.amount, l.bank_reference, l.description,
                   ch.payment_id, ch.payment_no, ch.version, ch.value_date, ch.bank_reference, ch.basis
            FROM fin.bank_statement_line l
            LEFT JOIN chosen ch ON ch.line_id = l.line_id
            WHERE l.company_id = @c AND l.statement_id = @s AND l.status = 'UNMATCHED' AND l.direction = 'DEBIT'
            ORDER BY l.value_date, l.line_id, ch.basis, ch.payment_no
            """,
            r => new Row(
                r.GetGuid(0), r.GetInt64(1), r.Date(2), r.GetDecimal(3), r.NullableString(4), r.GetString(5),
                r.NullableGuid(6), r.NullableString(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.Date(9), r.NullableString(10), r.NullableString(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.StatementId),
            ("window", MatchBankLineHandler.MatchWindowDays)).ConfigureAwait(false);

        var lines = rows
            .GroupBy(r => r.LineId)
            .Select(g =>
            {
                var first = g.First();
                var candidates = g.Where(r => r.PaymentId is not null)
                    .Select(r => new MatchCandidate(r.PaymentId!.Value, r.PaymentNo!, r.PaymentVersion!.Value, r.PaymentValueDate!.Value, r.PaymentReference, r.Basis!))
                    .ToList();
                return new LineSuggestion(first.LineId, first.LineVersion, first.ValueDate, Payments.PaymentRules.Money(first.Amount), first.LineReference, first.Description, candidates);
            })
            .ToList();
        return ApiJson.Serialize(new MatchSuggestions(query.StatementId, lines));
    }
}
