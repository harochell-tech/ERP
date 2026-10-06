using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.FixedAssets.Cards;
using Rochell.FixedAssets.Depreciation;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.FixedAssets.Disposals;

/// <summary>
/// E-AF-7, E-AF1-03-7/8/10: the Contador prepares the disposal of a whole card — SCRAP, or SALE at <paramref name="Price"/> — on
/// <paramref name="DisposalDate"/> (not in the future), once it is depreciated up to the month before; the month of the disposal is not
/// depreciated.
/// </summary>
public sealed record PrepareAssetDisposal(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AssetId, string Kind, DateOnly DisposalDate, decimal? Price, string Reason) : ICommand;

/// <summary>E-AF1-03-10: the Contador cancels a DRAFT disposal.</summary>
public sealed record CancelAssetDisposal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DisposalId, long ExpectedVersion) : ICommand;

/// <summary>
/// E-AF-7, E-AF1-03-6/9/10: the Controller (not the preparer, step-up) approves and posts P-45 on the disposal date: the accumulated
/// depreciation and the cost leave, the price goes to «Venta de activos por cobrar», the difference to gain or loss; the card is DISPOSED.
/// </summary>
public sealed record ApproveAssetDisposal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DisposalId, long ExpectedVersion) : ICommand;

public static class DisposalErrors
{
    public const string NotFound = "ASSET_DISPOSAL_NOT_FOUND";
    public const string Invalid = "ASSET_DISPOSAL_INVALID";

    /// <summary>E-AF1-03-7: the card still has months to depreciate before the disposal's month.</summary>
    public const string DepreciationPending = "FIXED_ASSET_DEPRECIATION_PENDING";
}

internal static class AssetDisposalBook
{
    public const string Aggregate = "AssetDisposal";
    public const string Scrap = "SCRAP";
    public const string Sale = "SALE";

    public sealed record Row(Guid Id, Guid AssetId, string Kind, DateOnly Date, decimal? Price, string Reason, string Status, Guid PreparedBy, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT disposal_id, asset_id, kind, disposal_date, price, reason, status, prepared_by, version FROM fa.asset_disposal WHERE company_id = @c AND disposal_id = @d FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Date(3), r.NullableDecimal(4), r.GetString(5), r.GetString(6), r.GetGuid(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", id)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(DisposalErrors.NotFound, "The disposal does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(FixedAssetErrors.VersionConflict, $"The disposal changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    /// <summary>
    /// E-AF1-03-7: a live card, bought on or before the date, the date not in the future, and — in service with something left to
    /// depreciate — its next month to depreciate is the disposal's month; fully depreciated, the date is after its last depreciated month.
    /// </summary>
    public static async Task RequireDisposableAsync(CommandContext context, FixedAssetCards.CardRow card, DateOnly date, CancellationToken cancellationToken)
    {
        if (card.Status != FixedAssetCards.InService)
        {
            throw new DomainException(
                FixedAssetErrors.InvalidState,
                $"{card.Number} is {card.Status}: only a card in service is disposed of (one awaiting service is put into service first, E-AF1-03-11).");
        }

        if (date < card.AcquiredOn || date > FixedAssetCards.BusinessDate(context))
        {
            throw new DomainException(DisposalErrors.Invalid, $"{card.Number} is disposed of between its purchase ({card.AcquiredOn:yyyy-MM-dd}) and today.");
        }

        var state = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"SELECT {DepreciationBook.NextMonthSql}, x.months_depreciated < x.useful_life_months AND {DepreciationBook.RemainingSql} > 0 FROM fa.asset x WHERE x.asset_id = @a",
            r => (Next: r.Date(0), Pending: r.GetBoolean(1)),
            cancellationToken,
            ("a", card.Id)).ConfigureAwait(false)).Single();
        var month = new DateOnly(date.Year, date.Month, 1);
        if (state.Pending && state.Next < month)
        {
            throw new DomainException(
                DisposalErrors.DepreciationPending,
                $"{card.Number} is depreciated up to the month before its disposal first; its next month to depreciate is {DepreciationBook.MonthText(state.Next)} (E-AF1-03-7).");
        }

        if (card.MonthsDepreciated > 0 && state.Next > month)
        {
            throw new DomainException(DisposalErrors.Invalid, $"{card.Number} was depreciated in {DepreciationBook.MonthText(month)}; it is disposed of after that month.");
        }
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class PrepareAssetDisposalHandler : ICommandHandler<PrepareAssetDisposal>
{
    public string CommandType => "FixedAssets.PrepareAssetDisposal";

    public async Task<string> HandleAsync(PrepareAssetDisposal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length is < 3 or > 300)
        {
            throw new DomainException(DisposalErrors.Invalid, "The reason has 3 to 300 characters.");
        }

        var price = command.Kind switch
        {
            AssetDisposalBook.Scrap when command.Price is null => (decimal?)null,
            AssetDisposalBook.Sale when command.Price is > 0 && command.Price == decimal.Round(command.Price.Value, 2) => command.Price,
            _ => throw new DomainException(DisposalErrors.Invalid, "A SCRAP has no price; a SALE has a price above zero with up to 2 decimals."),
        };
        var card = await FixedAssetCards.LockAsync(context, command.AssetId, null, cancellationToken).ConfigureAwait(false);
        await AssetDisposalBook.RequireDisposableAsync(context, card, command.DisposalDate, cancellationToken).ConfigureAwait(false);
        var live = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT 1 FROM fa.asset_disposal WHERE asset_id = @a AND status IN ('DRAFT', 'POSTED')", r => r.GetInt32(0), cancellationToken,
            ("a", card.Id)).ConfigureAwait(false)).Count > 0;
        if (live)
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"{card.Number} already has a disposal waiting for approval.");
        }

        var preparer = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AssetDisposalPrepared", 1, AssetDisposalBook.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new
                {
                    disposalId = context.ResultRef,
                    assetId = card.Id,
                    assetNo = card.Number,
                    kind = command.Kind,
                    disposalDate = command.DisposalDate,
                    price = price is { } p ? FixedAssetCards.Text(p) : null,
                    reason,
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fa.asset_disposal (disposal_id, company_id, asset_id, kind, disposal_date, price, reason, status, prepared_by, version)
            VALUES (@id, @c, @a, @k, @d, @p, @r, 'DRAFT', @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("a", card.Id),
            ("k", command.Kind),
            ("d", command.DisposalDate),
            ("p", (object?)price ?? DBNull.Value),
            ("r", reason),
            ("by", preparer)).ConfigureAwait(false);
        await context.AppendStateAsync(AssetDisposalBook.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { disposalId = context.ResultRef, assetNo = card.Number, status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class CancelAssetDisposalHandler : ICommandHandler<CancelAssetDisposal>
{
    public string CommandType => "FixedAssets.CancelAssetDisposal";

    public async Task<string> HandleAsync(CancelAssetDisposal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await AssetDisposalBook.LockAsync(context, command.DisposalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The disposal is {row.Status}: only a draft is cancelled.");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("AssetDisposalCancelled", 1, AssetDisposalBook.Aggregate, row.Id, version, JsonSerializer.Serialize(new { disposalId = row.Id }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fa.asset_disposal SET status = 'CANCELLED', version = @v WHERE disposal_id = @d", cancellationToken,
            ("v", version), ("d", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(AssetDisposalBook.Aggregate, row.Id, "DOCUMENT", "DRAFT", "CANCELLED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { disposalId = row.Id, status = "CANCELLED", version });
    }
}

[RequiresPermission("fixed_asset:approve", StepUp = true)]
public sealed class ApproveAssetDisposalHandler : ICommandHandler<ApproveAssetDisposal>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "FixedAssets.ApproveAssetDisposal";

    public async Task<string> HandleAsync(ApproveAssetDisposal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await AssetDisposalBook.LockAsync(context, command.DisposalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The disposal is {row.Status}: only a draft is approved.");
        }

        var approver = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FixedAssetErrors.ApproverIsCreator, "Who prepared a disposal does not approve it (E-AF-7).");
        }

        // The card may have changed since the draft (a settlement, a month depreciated): checked again under its lock.
        var card = await FixedAssetCards.LockAsync(context, row.AssetId, null, cancellationToken).ConfigureAwait(false);
        await AssetDisposalBook.RequireDisposableAsync(context, card, row.Date, cancellationToken).ConfigureAwait(false);
        var accounts = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.account_id, k.accumulated_account_id, x.description
            FROM fa.asset x JOIN pur.expense_category c ON c.expense_category_id = x.expense_category_id LEFT JOIN fa.asset_class k ON k.asset_class_id = x.asset_class_id
            WHERE x.asset_id = @a
            """,
            r => (Cost: r.GetGuid(0), Accumulated: r.NullableGuid(1), Description: r.GetString(2)),
            cancellationToken,
            ("a", card.Id)).ConfigureAwait(false)).Single();

        var price = row.Price ?? 0m;
        var book = card.Cost - card.Accumulated;
        var gain = Math.Max(price - book, 0m);
        var loss = Math.Max(book - price, 0m);
        var values = new Dictionary<string, string> { ["asset"] = card.Number, ["description"] = accounts.Description, ["kind"] = row.Kind, ["asset_id"] = card.Id.ToString() };
        var inputs = new List<PostingLineInput>();
        if (card.Accumulated > 0m)
        {
            inputs.Add(new PostingLineInput("P45-DR-ACC", "accumulated", card.Accumulated, PlantId: card.PlantId, AccountId: accounts.Accumulated, Inputs: values));
        }

        inputs.Add(new PostingLineInput("P45-DR-REC", "price", price, PlantId: card.PlantId, Inputs: values));
        inputs.Add(new PostingLineInput("P45-DR-LOSS", "loss", loss, PlantId: card.PlantId, Inputs: values));
        inputs.Add(new PostingLineInput("P45-CR-COST", "cost", card.Cost, PlantId: card.PlantId, AccountId: accounts.Cost, Inputs: values));
        inputs.Add(new PostingLineInput("P45-CR-GAIN", "gain", gain, PlantId: card.PlantId, Inputs: values));
        var now = context.Clock.UtcNow;
        var plan = await _engine.PrepareAsync(context, new PostingRequest("P-45", row.Date, now, inputs), cancellationToken).ConfigureAwait(false);

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AssetDisposalPosted",
                1,
                AssetDisposalBook.Aggregate,
                row.Id,
                version,
                JsonSerializer.Serialize(new
                {
                    disposalId = row.Id,
                    assetId = card.Id,
                    assetNo = card.Number,
                    kind = row.Kind,
                    cost = FixedAssetCards.Text(card.Cost),
                    accumulated = FixedAssetCards.Text(card.Accumulated),
                    bookValue = FixedAssetCards.Text(book),
                    price = FixedAssetCards.Text(price),
                    gain = FixedAssetCards.Text(gain),
                    loss = FixedAssetCards.Text(loss),
                    approvedBy = approver,
                }),
                Publish: true,
                OccurredAt: now,
                BusinessDate: row.Date),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fa.asset_disposal SET status = 'POSTED', approved_by = @by, approved_at = @at, posting_event_id = @e, version = @v WHERE disposal_id = @d",
            cancellationToken,
            ("by", approver),
            ("at", now),
            ("e", eventId),
            ("v", version),
            ("d", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(AssetDisposalBook.Aggregate, row.Id, "DOCUMENT", "DRAFT", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fa.asset SET status = 'DISPOSED', version = version + 1 WHERE asset_id = @a", cancellationToken, ("a", card.Id))
            .ConfigureAwait(false);
        await FixedAssetCards.MovementAsync(context, card.Id, "DISPOSAL", row.Date, card.Cost, card.PlantId, row.Id, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(FixedAssetCards.Aggregate, card.Id, "DOCUMENT", card.Status, FixedAssetCards.Disposed, CommandType, eventId, cancellationToken, row.Reason)
            .ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            disposalId = row.Id,
            status = "POSTED",
            bookValue = FixedAssetCards.Text(book),
            gain = FixedAssetCards.Text(gain),
            loss = FixedAssetCards.Text(loss),
            journals = new[] { journal.JournalId },
            version,
        });
    }
}

/// <summary>E-AF-7: the disposals, by status when given, newest first.</summary>
public sealed record ListAssetDisposals(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record AssetDisposalView(
    Guid DisposalId, Guid AssetId, string AssetNo, string Description, string Kind, DateOnly DisposalDate, decimal? Price, string Reason, string Status, string? PreparedBy,
    string? ApprovedBy, long Version);

public sealed record AssetDisposalList(IReadOnlyList<AssetDisposalView> Items);

[RequiresPermission("ledger:read")]
public sealed class ListAssetDisposalsHandler : IQueryHandler<ListAssetDisposals>
{
    public string QueryType => "FixedAssets.ListAssetDisposals";

    public async Task<string> HandleAsync(ListAssetDisposals query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Status is not (null or "DRAFT" or "POSTED" or "CANCELLED"))
        {
            throw new DomainException(QueryErrors.InvalidParameter, "status is DRAFT, POSTED or CANCELLED.");
        }

        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.disposal_id, d.asset_id, x.asset_no, x.description, d.kind, d.disposal_date, d.price::numeric(19,2), d.reason, d.status,
                   coalesce(p.display_name, p.email), coalesce(a.display_name, a.email), d.version
            FROM fa.asset_disposal d
            JOIN fa.asset x ON x.asset_id = d.asset_id
            LEFT JOIN iam.user p ON p.user_id = d.prepared_by
            LEFT JOIN iam.user a ON a.user_id = d.approved_by
            WHERE d.company_id = @c AND (CAST(@s AS text) IS NULL OR d.status = CAST(@s AS text))
            ORDER BY d.disposal_date DESC, x.asset_no
            """,
            r => new AssetDisposalView(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Date(5), r.NullableDecimal(6), r.GetString(7), r.GetString(8), r.NullableString(9),
                r.NullableString(10), r.GetInt64(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new AssetDisposalList(items));
    }
}
