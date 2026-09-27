using Rochell.Platform.Commands;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Reconciliation.Queries;

/// <summary>
/// E-VS2-07-5: BANK-GL of one bank account at a date (today's business date by default), computed read-only with the same logic
/// as the reconciliation; nothing is stored (only RunReconciliation and CloseComponent store runs).
/// </summary>
public sealed record GetBankReconciliation(Guid CompanyId, Guid SessionId, Guid BankAccountId, DateOnly? AsOf = null) : IQuery;

public sealed record BankReconciliationFinding(string Classification, string Severity, string MatchKey, decimal? ValueA, decimal? ValueB);

/// <summary><see cref="Skipped"/>: no movement and a zero balance up to the date (E-VS2-06-8).</summary>
public sealed record BankReconciliationView(
    Guid BankAccountId,
    DateOnly AsOf,
    bool Skipped,
    decimal? GlBalance,
    decimal? StatementBalance,
    decimal? Difference,
    IReadOnlyList<BankItem> GlItems,
    IReadOnlyList<BankItem> LineItems,
    IReadOnlyList<BankReconciliationFinding> Findings);

[RequiresPermission("bank:read")]
public sealed class GetBankReconciliationHandler : IQueryHandler<GetBankReconciliation>
{
    public string QueryType => "Reconciliation.GetBankReconciliation";

    public async Task<string> HandleAsync(GetBankReconciliation query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        await using (var exists = Rochell.Platform.Data.Sql.Command(
            context.Connection, context.Transaction, "SELECT 1 FROM fin.bank_account WHERE company_id = @c AND bank_account_id = @b", ("c", context.CompanyId), ("b", query.BankAccountId)))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new DomainException(QueryErrors.NotFound, "The bank account does not exist.");
            }
        }

        var asOf = query.AsOf ?? BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var (findings, accounts) = await BankGl.ReadAsync(context.Connection, context.Transaction, context.CompanyId, asOf, query.BankAccountId, cancellationToken).ConfigureAwait(false);
        var findingViews = findings.Select(f => new BankReconciliationFinding(f.Classification, f.Severity, f.MatchKey, f.ValueA, f.ValueB)).ToList();
        var view = accounts.SingleOrDefault() is { } a
            ? new BankReconciliationView(query.BankAccountId, a.Cutoff, false, a.GlBalance, a.StatementBalance, a.Difference, a.GlItems, a.LineItems, findingViews)
            : new BankReconciliationView(query.BankAccountId, asOf, true, null, null, null, [], [], findingViews);
        return ApiJson.Serialize(view);
    }
}
