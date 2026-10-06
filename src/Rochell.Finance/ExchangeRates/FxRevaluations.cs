using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Finance.ExchangeRates;

/// <summary>
/// E-USD1-06-1/2/3: revalues the open USD balances of the month of <paramref name="Month"/> (any day of it) at the approved rate of its last day
/// (the last before, on a weekend): each USD payable and each USD bank account moves to its USD × rate as of that day (P-43 on the last day),
/// and the exact opposite is posted on the next day (P-43R). One per month; the month must have ended.
/// </summary>
public sealed record PostFxRevaluation(Guid CompanyId, Guid SessionId, string IdempotencyKey, DateOnly Month) : ICommand;

/// <summary>E-USD1-06-3: both journals of a POSTED revaluation are reversed so the month can be revalued again.</summary>
public sealed record UndoFxRevaluation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RevaluationId, long ExpectedVersion, string Reason) : ICommand;

public static class FxRevaluationErrors
{
    public const string MonthNotEnded = "FX_MONTH_NOT_ENDED";
    public const string AlreadyRevalued = "FX_ALREADY_REVALUED";
    public const string NothingToRevalue = "FX_NOTHING_TO_REVALUE";
    public const string NotFound = "FX_REVALUATION_NOT_FOUND";
    public const string InvalidState = "FX_REVALUATION_INVALID_STATE";
}

internal static class FxRevaluationBook
{
    public const string Aggregate = "FxRevaluation";

    /// <summary>A USD balance as of the month's end: its ledger pesos (every line) and its USD (the USD lines).</summary>
    public sealed record Balance(string Kind, Guid Ref, Guid? PartyId, string Number, decimal Pesos, decimal Usd);

    /// <summary>
    /// The USD payables (AP_FOREIGN by AP document) and USD bank accounts (BANK by account) with their balances posted on or before
    /// <paramref name="end"/>; a payable's pesos are what it owes (credit − debit), a bank's what it holds (debit − credit).
    /// </summary>
    public static async Task<IReadOnlyList<Balance>> BalancesAsync(CommandContext context, DateOnly end, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT 'AP', e.subledger_ref, min(e.party_id::text)::uuid, coalesce(min(s.doc_number), e.subledger_ref::text),
                   sum(e.credit - e.debit), coalesce(sum(CASE WHEN e.currency = 'USD' THEN sign(e.credit - e.debit) * e.amount_fc END), 0)
            FROM fin.gl_entry e LEFT JOIN fin.ap_source s ON s.ap_doc_id = e.subledger_ref
            WHERE e.company_id = @c AND e.account_role = 'AP_FOREIGN' AND e.posting_date <= @end
            GROUP BY e.subledger_ref
            UNION ALL
            SELECT 'BANK', b.bank_account_id, NULL, b.bank_code || ' ' || right(b.account_number, 4),
                   coalesce(sum(e.debit - e.credit), 0), coalesce(sum(CASE WHEN e.currency = 'USD' THEN sign(e.debit - e.credit) * e.amount_fc END), 0)
            FROM fin.bank_account b
            LEFT JOIN fin.gl_entry e ON e.subledger_type = 'BANK' AND e.subledger_ref = b.bank_account_id AND e.posting_date <= @end
            WHERE b.company_id = @c AND b.currency = 'USD'
            GROUP BY b.bank_account_id, b.bank_code, b.account_number
            ORDER BY 1, 2
            """,
            r => new Balance(r.GetString(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("end", end)).ConfigureAwait(false);

    /// <summary>The P-43 (or P-43R, with every side swapped) lines of the differences, and the net to FX_UNREALIZED.</summary>
    public static List<PostingLineInput> Lines(string prefix, IReadOnlyList<(Balance Balance, decimal Difference)> moves, bool opposite, Dictionary<string, string> common)
    {
        var lines = new List<PostingLineInput>();
        var loss = 0m;
        foreach (var (b, d) in moves)
        {
            var difference = opposite ? -d : d;
            var inputs = new Dictionary<string, string>(common) { ["number"] = b.Number, ["amount_usd"] = b.Usd.ToString("0.00", CultureInfo.InvariantCulture) };
            if (b.Kind == "AP")
            {
                // A payable that grows in pesos is a loss; one that shrinks a gain.
                lines.Add(difference > 0m
                    ? new PostingLineInput($"{prefix}-CR-AP", "ap_increase", difference, PartyId: b.PartyId, SubledgerRef: b.Ref, Inputs: inputs)
                    : new PostingLineInput($"{prefix}-DR-AP", "ap_decrease", -difference, PartyId: b.PartyId, SubledgerRef: b.Ref, Inputs: inputs));
                loss += difference;
            }
            else
            {
                // A bank that grows in pesos is a gain.
                lines.Add(difference > 0m
                    ? new PostingLineInput($"{prefix}-DR-BANK", "bank_increase", difference, SubledgerRef: b.Ref, Inputs: inputs)
                    : new PostingLineInput($"{prefix}-CR-BANK", "bank_decrease", -difference, SubledgerRef: b.Ref, Inputs: inputs));
                loss -= difference;
            }
        }

        lines.Add(new PostingLineInput($"{prefix}-DR-FXU", "fx_loss", Math.Max(loss, 0m), Inputs: common));
        lines.Add(new PostingLineInput($"{prefix}-CR-FXU", "fx_gain", Math.Max(-loss, 0m), Inputs: common));
        return lines;
    }

    public static string MonthText(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}

[RequiresPermission("fx_revaluation:post", StepUp = true)]
public sealed class PostFxRevaluationHandler : ICommandHandler<PostFxRevaluation>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Finance.PostFxRevaluation";

    public async Task<string> HandleAsync(PostFxRevaluation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var month = new DateOnly(command.Month.Year, command.Month.Month, 1);
        var end = month.AddMonths(1).AddDays(-1);
        var now = context.Clock.UtcNow;
        if (end > BusinessCalendar.DefaultBusinessDate(now))
        {
            throw new DomainException(FxRevaluationErrors.MonthNotEnded, $"{FxRevaluationBook.MonthText(month)} is revalued on or after its last day.");
        }

        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('fx-revaluation:' || @c, 0))", cancellationToken,
            ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var existing = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT revaluation_id FROM fin.fx_revaluation WHERE company_id = @c AND month = @m AND status = 'POSTED'", r => r.GetGuid(0),
            cancellationToken, ("c", context.CompanyId), ("m", month)).ConfigureAwait(false)).Any();
        if (existing)
        {
            throw new DomainException(FxRevaluationErrors.AlreadyRevalued, $"{FxRevaluationBook.MonthText(month)} is already revalued; undo it to revalue it again (E-USD1-06-3).");
        }

        var rate = await ExchangeRateBook.ForDateAsync(context.Connection, context.Transaction, context.CompanyId, "USD", end, cancellationToken).ConfigureAwait(false);
        var moves = (await FxRevaluationBook.BalancesAsync(context, end, cancellationToken).ConfigureAwait(false))
            .Select(b => (Balance: b, Difference: ExchangeRateBook.ToPesos(b.Usd, rate.Rate) - b.Pesos))
            .Where(m => m.Difference != 0m)
            .ToList();
        if (moves.Count == 0)
        {
            throw new DomainException(FxRevaluationErrors.NothingToRevalue, $"No USD balance of {FxRevaluationBook.MonthText(month)} differs from its value at {rate.Rate:0.0000}.");
        }

        var common = new Dictionary<string, string> { ["month"] = FxRevaluationBook.MonthText(month), ["rate"] = rate.Rate.ToString("0.0000", CultureInfo.InvariantCulture) };
        var plan = await _engine.PrepareAsync(context, new PostingRequest("P-43", end, now, FxRevaluationBook.Lines("P43", moves, opposite: false, common)), cancellationToken)
            .ConfigureAwait(false);
        var next = end.AddDays(1);
        var reversalPlan = await _engine.PrepareAsync(context, new PostingRequest("P-43R", next, now, FxRevaluationBook.Lines("P43R", moves, opposite: true, common)), cancellationToken)
            .ConfigureAwait(false);

        var id = context.ResultRef;
        var difference = moves.Sum(m => m.Balance.Kind == "AP" ? m.Difference : -m.Difference);
        var poster = await ExchangeRateBook.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new
        {
            revaluationId = id,
            month = FxRevaluationBook.MonthText(month),
            rateDate = rate.RateDate,
            rate = rate.Rate.ToString("0.0000", CultureInfo.InvariantCulture),
            loss = difference.ToString("0.00", CultureInfo.InvariantCulture),
            balances = moves.Select(m => new
            {
                kind = m.Balance.Kind,
                reference = m.Balance.Ref,
                usd = m.Balance.Usd.ToString("0.00", CultureInfo.InvariantCulture),
                pesos = m.Balance.Pesos.ToString("0.00", CultureInfo.InvariantCulture),
                difference = m.Difference.ToString("0.00", CultureInfo.InvariantCulture),
            }),
        });
        var posted = await context.AppendEventAsync(
            new EventDraft("FxRevaluationPosted", 1, FxRevaluationBook.Aggregate, id, 1, payload, Publish: true, OccurredAt: now, BusinessDate: end), cancellationToken).ConfigureAwait(false);
        var reversed = await context.AppendEventAsync(
            new EventDraft("FxRevaluationReversed", 1, FxRevaluationBook.Aggregate, id, 2, JsonSerializer.Serialize(new { revaluationId = id, businessDate = next }), Publish: true,
                OccurredAt: now, BusinessDate: next, CausationId: posted),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.fx_revaluation (revaluation_id, company_id, month, rate_date, rate, difference, status, posted_by, posting_event_id, reversal_event_id, version)
            VALUES (@id, @c, @m, @rd, @r, @d, 'POSTED', @by, @pe, @re, 1)
            """,
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("m", month),
            ("rd", rate.RateDate),
            ("r", rate.Rate),
            ("d", difference),
            ("by", poster),
            ("pe", posted),
            ("re", reversed)).ConfigureAwait(false);
        await context.AppendStateAsync(FxRevaluationBook.Aggregate, id, "DOCUMENT", null, "POSTED", CommandType, posted, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, posted, cancellationToken).ConfigureAwait(false);
        var reversal = await _engine.WriteAsync(context, reversalPlan, reversed, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            revaluationId = id,
            month = FxRevaluationBook.MonthText(month),
            rate = rate.Rate.ToString("0.0000", CultureInfo.InvariantCulture),
            loss = difference.ToString("0.00", CultureInfo.InvariantCulture),
            journals = new[] { journal.JournalId, reversal.JournalId },
            version = 1,
        });
    }
}

[RequiresPermission("fx_revaluation:post", StepUp = true)]
public sealed class UndoFxRevaluationHandler : ICommandHandler<UndoFxRevaluation>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Finance.UndoFxRevaluation";

    public async Task<string> HandleAsync(UndoFxRevaluation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length < 10)
        {
            throw new DomainException(FxRevaluationErrors.InvalidState, "Undoing a revaluation needs a reason of at least 10 characters.");
        }

        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, version, posting_event_id, reversal_event_id, month FROM fin.fx_revaluation WHERE company_id = @c AND revaluation_id = @r FOR UPDATE",
            r => (Status: r.GetString(0), Version: r.GetInt64(1), Posted: r.GetGuid(2), Reversed: r.GetGuid(3), Month: r.Date(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", command.RevaluationId)).ConfigureAwait(false)).SingleOrDefault();
        if (row == default)
        {
            throw new DomainException(FxRevaluationErrors.NotFound, "The revaluation does not exist.");
        }

        if (row.Status != "POSTED" || row.Version != command.ExpectedVersion)
        {
            throw new DomainException(FxRevaluationErrors.InvalidState, $"The revaluation is {row.Status} at version {row.Version}.");
        }

        var journals = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT journal_id, posting_date FROM fin.gl_journal WHERE company_id = @c AND source_event_id = ANY(@e) AND journal_type = 'AUTO' ORDER BY posting_date",
            r => (Id: r.GetGuid(0), Date: r.Date(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", new[] { row.Posted, row.Reversed })).ConfigureAwait(false);
        var now = context.Clock.UtcNow;

        // Each journal is reversed on its own date, so the month-end and the next day both return to the invoices' pesos.
        var plans = new List<ReversalPlan>();
        foreach (var journal in journals)
        {
            plans.Add(await _engine.PrepareReversalAsync(context, journal.Id, journal.Date, cancellationToken).ConfigureAwait(false));
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("FxRevaluationUndone", 1, FxRevaluationBook.Aggregate, command.RevaluationId, version + 1,
                JsonSerializer.Serialize(new { revaluationId = command.RevaluationId, reason }), Publish: true, OccurredAt: now),
            cancellationToken).ConfigureAwait(false);
        var reversals = new List<Guid>();
        foreach (var plan in plans)
        {
            reversals.Add((await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false)).JournalId);
        }

        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.fx_revaluation SET status = 'UNDONE', version = @v WHERE revaluation_id = @r", cancellationToken,
            ("v", version), ("r", command.RevaluationId)).ConfigureAwait(false);
        await context.AppendStateAsync(FxRevaluationBook.Aggregate, command.RevaluationId, "DOCUMENT", "POSTED", "UNDONE", CommandType, eventId, cancellationToken, reason)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { revaluationId = command.RevaluationId, status = "UNDONE", journals = reversals, version });
    }
}

/// <summary>E-USD1-06-1: the month-end revaluations, newest first.</summary>
public sealed record ListFxRevaluations(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record FxRevaluationView(Guid RevaluationId, string Month, DateOnly RateDate, decimal Rate, decimal Loss, string Status, string? PostedBy, long Version);

public sealed record FxRevaluationList(IReadOnlyList<FxRevaluationView> Items);

[RequiresPermission("exchange_rate:read")]
public sealed class ListFxRevaluationsHandler : IQueryHandler<ListFxRevaluations>
{
    public string QueryType => "Finance.ListFxRevaluations";

    public async Task<string> HandleAsync(ListFxRevaluations query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.revaluation_id, to_char(x.month, 'YYYY-MM'), x.rate_date, x.rate, x.difference, x.status, coalesce(u.display_name, u.email), x.version
            FROM fin.fx_revaluation x LEFT JOIN iam.user u ON u.user_id = x.posted_by
            WHERE x.company_id = @c ORDER BY x.month DESC, x.version DESC
            """,
            r => new FxRevaluationView(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetDecimal(3), r.GetDecimal(4), r.GetString(5), r.NullableString(6), r.GetInt64(7)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new FxRevaluationList(items));
    }
}
