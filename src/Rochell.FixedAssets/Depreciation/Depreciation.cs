using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.FixedAssets.Cards;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.FixedAssets.Depreciation;

/// <summary>
/// E-AF-6, E-AF1-03-1…4: the month's depreciation of every card in service (any day of <paramref name="Month"/>), posted on its last day
/// (P-44) and from that day on: (cost − residual − accumulated) ÷ months left, 2 decimals, the last month exact. Months go in order.
/// </summary>
public sealed record PostDepreciation(Guid CompanyId, Guid SessionId, string IdempotencyKey, DateOnly Month) : ICommand;

/// <summary>E-AF1-03-5: undoes the latest POSTED month while its period is open and none of its cards was disposed of since.</summary>
public sealed record UndoDepreciation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RunId, long ExpectedVersion, string Reason) : ICommand;

public static class DepreciationErrors
{
    public const string MonthNotEnded = "DEPRECIATION_MONTH_NOT_ENDED";
    public const string AlreadyPosted = "DEPRECIATION_ALREADY_POSTED";
    public const string MonthSkipped = "DEPRECIATION_MONTH_SKIPPED";
    public const string Nothing = "DEPRECIATION_NOTHING";
    public const string NotLatest = "DEPRECIATION_NOT_LATEST";
    public const string RunNotFound = "DEPRECIATION_RUN_NOT_FOUND";
}

/// <summary>What a card depreciates in a month and where (E-AF1-03-3, E-AF1-02-5).</summary>
internal sealed record DepreciationLine(
    Guid AssetId, string AssetNo, string Description, Guid PlantId, Guid ExpenseAccountId, Guid AccumulatedAccountId, int Month, int Life, decimal Amount, long Version);

internal static class DepreciationBook
{
    public const string Aggregate = "DepreciationRun";

    /// <summary>The first month a card in service still has to depreciate: the month after its service, plus what it already depreciated.</summary>
    public const string NextMonthSql = "(date_trunc('month', x.in_service_on) + make_interval(months => 1 + x.months_depreciated))::date";

    /// <summary>What remains to depreciate: cost − residual (cost × residual %, 2 decimals) − accumulated.</summary>
    public const string RemainingSql = "(x.cost - round(x.cost * x.residual_pct / 100, 2) - x.accumulated)";

    public static string MonthText(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>E-AF1-03-2: the earliest month before <paramref name="month"/> that a card still had to depreciate, if any.</summary>
    public static async Task<DateOnly?> SkippedMonthAsync(CommandContext context, DateOnly month, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT min({NextMonthSql}) FROM fa.asset x
            WHERE x.company_id = @c AND x.status = 'IN_SERVICE' AND x.months_depreciated < x.useful_life_months AND {RemainingSql} > 0 AND {NextMonthSql} < @m
            """,
            r => r.IsDBNull(0) ? (DateOnly?)null : r.Date(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("m", month)).ConfigureAwait(false)).Single();

    /// <summary>E-AF1-03-3/4: each card due in <paramref name="month"/>, its amount and the plant in force on the month's last day.</summary>
    public static async Task<IReadOnlyList<DepreciationLine>> LinesAsync(CommandContext context, DateOnly month, CancellationToken cancellationToken)
    {
        var end = month.AddMonths(1).AddDays(-1);
        var due = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT x.asset_id, x.asset_no, x.description,
                   coalesce((SELECT m.plant_id FROM fa.asset_movement m JOIN core.domain_event e ON e.event_id = m.event_id
                             WHERE m.asset_id = x.asset_id AND m.plant_id IS NOT NULL AND m.movement_date <= @end
                             ORDER BY m.movement_date DESC, e.recorded_at DESC LIMIT 1), x.plant_id),
                   k.expense_account_id, k.accumulated_account_id, x.months_depreciated, x.useful_life_months, {RemainingSql}, x.version
            FROM fa.asset x JOIN fa.asset_class k ON k.asset_class_id = x.asset_class_id
            WHERE x.company_id = @c AND x.status = 'IN_SERVICE' AND x.months_depreciated < x.useful_life_months AND {RemainingSql} > 0 AND {NextMonthSql} = @m
            ORDER BY x.asset_no
            FOR UPDATE OF x
            """,
            r => (Id: r.GetGuid(0), No: r.GetString(1), Description: r.GetString(2), Plant: r.GetGuid(3), Expense: r.GetGuid(4), Accumulated: r.GetGuid(5), Done: r.GetInt32(6),
                Life: r.GetInt32(7), Remaining: r.GetDecimal(8), Version: r.GetInt64(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("m", month),
            ("end", end)).ConfigureAwait(false);
        return
        [
            .. due.Select(d =>
            {
                var monthsLeft = d.Life - d.Done;
                var amount = monthsLeft == 1 ? d.Remaining : decimal.Round(d.Remaining / monthsLeft, 2, MidpointRounding.AwayFromZero);
                return new DepreciationLine(d.Id, d.No, d.Description, d.Plant, d.Expense, d.Accumulated, d.Done + 1, d.Life, amount, d.Version);
            }),
        ];
    }

    public static List<PostingLineInput> PostingLines(DateOnly month, IReadOnlyList<DepreciationLine> lines)
    {
        var inputs = new List<PostingLineInput>(lines.Count * 2);
        foreach (var l in lines)
        {
            var values = new Dictionary<string, string>
            {
                ["month"] = MonthText(month),
                ["asset"] = l.AssetNo,
                ["description"] = l.Description,
                ["months"] = l.Month.ToString(CultureInfo.InvariantCulture),
                ["life"] = l.Life.ToString(CultureInfo.InvariantCulture),
                ["asset_id"] = l.AssetId.ToString(),
            };
            inputs.Add(new PostingLineInput("P44-DR-DEP", "depreciation", l.Amount, PlantId: l.PlantId, AccountId: l.ExpenseAccountId, Inputs: values));
            inputs.Add(new PostingLineInput("P44-CR-ACC", "depreciation", l.Amount, PlantId: l.PlantId, AccountId: l.AccumulatedAccountId, Inputs: values));
        }

        return inputs;
    }
}

[RequiresPermission("fixed_asset:manage", StepUp = true)]
public sealed class PostDepreciationHandler : ICommandHandler<PostDepreciation>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "FixedAssets.PostDepreciation";

    public async Task<string> HandleAsync(PostDepreciation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var month = new DateOnly(command.Month.Year, command.Month.Month, 1);
        var end = month.AddMonths(1).AddDays(-1);
        var now = context.Clock.UtcNow;
        if (end > FixedAssetCards.BusinessDate(context))
        {
            throw new DomainException(DepreciationErrors.MonthNotEnded, $"{DepreciationBook.MonthText(month)} is depreciated from its last day (E-AF1-03-1).");
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('fa-depreciation:' || @c, 0))", cancellationToken,
            ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var posted = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT run_id FROM fa.depreciation_run WHERE company_id = @c AND month = @m AND status = 'POSTED'", r => r.GetGuid(0),
            cancellationToken, ("c", context.CompanyId), ("m", month)).ConfigureAwait(false)).Count > 0;
        if (posted)
        {
            throw new DomainException(DepreciationErrors.AlreadyPosted, $"{DepreciationBook.MonthText(month)} is already depreciated; undo it to post it again.");
        }

        if (await DepreciationBook.SkippedMonthAsync(context, month, cancellationToken).ConfigureAwait(false) is { } skipped)
        {
            throw new DomainException(DepreciationErrors.MonthSkipped, $"{DepreciationBook.MonthText(skipped)} is not depreciated yet; months go in order (E-AF1-03-2).");
        }

        var lines = await DepreciationBook.LinesAsync(context, month, cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            throw new DomainException(DepreciationErrors.Nothing, $"No asset in service depreciates in {DepreciationBook.MonthText(month)}.");
        }

        var plan = await _engine.PrepareAsync(context, new PostingRequest("P-44", end, now, DepreciationBook.PostingLines(month, lines)), cancellationToken).ConfigureAwait(false);
        var id = context.ResultRef;
        var total = lines.Sum(l => l.Amount);
        var poster = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "DepreciationPosted",
                1,
                DepreciationBook.Aggregate,
                id,
                1,
                JsonSerializer.Serialize(new
                {
                    runId = id,
                    month = DepreciationBook.MonthText(month),
                    total = FixedAssetCards.Text(total),
                    lines = lines.Select(l => new { assetId = l.AssetId, plantId = l.PlantId, amount = FixedAssetCards.Text(l.Amount), month = l.Month }),
                }),
                Publish: true,
                OccurredAt: now,
                BusinessDate: end),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO fa.depreciation_run (run_id, company_id, month, total, status, posted_by, posting_event_id, version) VALUES (@id, @c, @m, @t, 'POSTED', @by, @e, 1)",
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("m", month),
            ("t", total),
            ("by", poster),
            ("e", eventId)).ConfigureAwait(false);
        foreach (var l in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction,
                "INSERT INTO fa.depreciation_line (run_id, company_id, asset_id, plant_id, amount) VALUES (@r, @c, @a, @p, @amount)",
                cancellationToken, ("r", id), ("c", context.CompanyId), ("a", l.AssetId), ("p", l.PlantId), ("amount", l.Amount)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction,
                "UPDATE fa.asset SET accumulated = accumulated + @amount, months_depreciated = months_depreciated + 1, version = version + 1 WHERE asset_id = @a",
                cancellationToken, ("amount", l.Amount), ("a", l.AssetId)).ConfigureAwait(false);
            await FixedAssetCards.MovementAsync(context, l.AssetId, "DEPRECIATION", end, l.Amount, l.PlantId, id, eventId, cancellationToken).ConfigureAwait(false);
        }

        await context.AppendStateAsync(DepreciationBook.Aggregate, id, "DOCUMENT", null, "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            runId = id,
            month = DepreciationBook.MonthText(month),
            assets = lines.Count,
            total = FixedAssetCards.Text(total),
            journals = new[] { journal.JournalId },
            version = 1,
        });
    }
}

[RequiresPermission("fixed_asset:manage", StepUp = true)]
public sealed class UndoDepreciationHandler : ICommandHandler<UndoDepreciation>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "FixedAssets.UndoDepreciation";

    public async Task<string> HandleAsync(UndoDepreciation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length < 10)
        {
            throw new DomainException(FixedAssetErrors.InvalidState, "Undoing a month's depreciation needs a reason of at least 10 characters.");
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('fa-depreciation:' || @c, 0))", cancellationToken,
            ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var run = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, version, posting_event_id, month FROM fa.depreciation_run WHERE company_id = @c AND run_id = @r FOR UPDATE",
            r => (Status: r.GetString(0), Version: r.GetInt64(1), Event: r.GetGuid(2), Month: r.Date(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", command.RunId)).ConfigureAwait(false)).SingleOrDefault();
        if (run == default)
        {
            throw new DomainException(DepreciationErrors.RunNotFound, "The depreciation run does not exist.");
        }

        if (run.Status != "POSTED" || run.Version != command.ExpectedVersion)
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The run is {run.Status} at version {run.Version}.");
        }

        var later = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT 1 FROM fa.depreciation_run WHERE company_id = @c AND status = 'POSTED' AND month > @m", r => r.GetInt32(0),
            cancellationToken, ("c", context.CompanyId), ("m", run.Month)).ConfigureAwait(false)).Count > 0;
        if (later)
        {
            throw new DomainException(DepreciationErrors.NotLatest, "Only the latest depreciated month is undone (E-AF1-03-5).");
        }

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.asset_id, l.amount, x.status, x.asset_no FROM fa.depreciation_line l JOIN fa.asset x ON x.asset_id = l.asset_id
            WHERE l.run_id = @r ORDER BY x.asset_no FOR UPDATE OF x
            """,
            r => (AssetId: r.GetGuid(0), Amount: r.GetDecimal(1), Status: r.GetString(2), No: r.GetString(3)),
            cancellationToken,
            ("r", command.RunId)).ConfigureAwait(false);
        var disposed = lines.Where(l => l.Status != FixedAssetCards.InService).Select(l => l.No).ToList();
        if (disposed.Count > 0)
        {
            throw new DomainException(FixedAssetErrors.AssetDisposed, $"{string.Join(", ", disposed)} was disposed of after this month; it is no longer undone (E-AF1-03-5).");
        }

        var journal = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT journal_id, posting_date FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'AUTO'",
            r => (Id: r.GetGuid(0), Date: r.Date(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", run.Event)).ConfigureAwait(false)).Single();
        var now = context.Clock.UtcNow;
        var plan = await _engine.PrepareReversalAsync(context, journal.Id, journal.Date, cancellationToken).ConfigureAwait(false);
        var version = run.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("DepreciationUndone", 1, DepreciationBook.Aggregate, command.RunId, version, JsonSerializer.Serialize(new { runId = command.RunId, reason }), Publish: true,
                OccurredAt: now, BusinessDate: journal.Date),
            cancellationToken).ConfigureAwait(false);
        foreach (var l in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction,
                "UPDATE fa.asset SET accumulated = accumulated - @amount, months_depreciated = months_depreciated - 1, version = version + 1 WHERE asset_id = @a",
                cancellationToken, ("amount", l.Amount), ("a", l.AssetId)).ConfigureAwait(false);
            await FixedAssetCards.MovementAsync(context, l.AssetId, "DEPRECIATION_UNDONE", journal.Date, l.Amount, null, command.RunId, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fa.depreciation_run SET status = 'UNDONE', version = @v WHERE run_id = @r", cancellationToken,
            ("v", version), ("r", command.RunId)).ConfigureAwait(false);
        await context.AppendStateAsync(DepreciationBook.Aggregate, command.RunId, "DOCUMENT", "POSTED", "UNDONE", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { runId = command.RunId, status = "UNDONE", journals = new[] { reversal.JournalId }, version });
    }
}

/// <summary>E-AF-6: the months depreciated, newest first.</summary>
public sealed record ListDepreciationRuns(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record DepreciationRunView(Guid RunId, string Month, decimal Total, int Assets, string Status, string? PostedBy, long Version);

public sealed record DepreciationRunList(IReadOnlyList<DepreciationRunView> Items);

[RequiresPermission("ledger:read")]
public sealed class ListDepreciationRunsHandler : IQueryHandler<ListDepreciationRuns>
{
    public string QueryType => "FixedAssets.ListDepreciationRuns";

    public async Task<string> HandleAsync(ListDepreciationRuns query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.run_id, to_char(r.month, 'YYYY-MM'), r.total::numeric(19,2), (SELECT count(*)::int FROM fa.depreciation_line l WHERE l.run_id = r.run_id), r.status,
                   coalesce(u.display_name, u.email), r.version
            FROM fa.depreciation_run r LEFT JOIN iam.user u ON u.user_id = r.posted_by
            WHERE r.company_id = @c ORDER BY r.month DESC, r.version DESC
            """,
            r => new DepreciationRunView(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetInt32(3), r.GetString(4), r.NullableString(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new DepreciationRunList(items));
    }
}
