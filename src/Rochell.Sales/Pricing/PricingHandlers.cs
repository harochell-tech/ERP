using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Pricing;

internal static class Pricing
{
    public const string CostAggregate = "StandardCost";
    public const string PriceAggregate = "PriceList";

    public static string Money4(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    public static async Task<string> FinishedGoodBaseUomAsync(CommandContext context, Guid itemId, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<string>(
               context, "SELECT base_uom FROM md.item WHERE company_id = @c AND item_id = @i AND item_type = 'FINISHED_GOOD'", cancellationToken,
               ("c", context.CompanyId), ("i", itemId)).ConfigureAwait(false)
           ?? throw new DomainException(SalesErrors.NotFinishedGood, $"Item {itemId} is not a finished good of the company.");

    public sealed record Approval(string Status, Guid PreparedBy);

    public static void EnsureApprovable(Approval row, Guid approver, string what)
    {
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The {what} is {row.Status}.");
        }

        if (approver == row.PreparedBy)
        {
            throw new DomainException(SalesErrors.FourEyes, $"The {what} is approved by someone other than who prepared it.");
        }
    }
}

[RequiresPermission("standard_cost:prepare")]
public sealed class PrepareStandardCostHandler : ICommandHandler<PrepareStandardCost>
{
    public string CommandType => "Sales.PrepareStandardCost";

    public async Task<string> HandleAsync(PrepareStandardCost command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var unitCost = SalesSql.Positive(command.UnitCost, 4, "The standard cost");
        await Pricing.FinishedGoodBaseUomAsync(context, command.ItemId, cancellationToken).ConfigureAwait(false);
        if (await SalesSql.ScalarAsync<Guid?>(
                context, "SELECT valuation_area_id FROM md.valuation_area WHERE company_id = @c AND valuation_area_id = @a", cancellationToken,
                ("c", context.CompanyId), ("a", command.ValuationAreaId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(SalesErrors.NotFound, "The valuation area does not exist.");
        }

        await SalesSql.LockAsync(context, $"standard-cost:{command.ItemId}:{command.ValuationAreaId}", cancellationToken).ConfigureAwait(false);
        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var draft = await SalesSql.ScalarAsync<Guid?>(
            context,
            "SELECT cost_version_id FROM md.standard_cost_version WHERE company_id = @c AND item_id = @i AND valuation_area_id = @a AND status = 'DRAFT'",
            cancellationToken,
            ("c", context.CompanyId),
            ("i", command.ItemId),
            ("a", command.ValuationAreaId)).ConfigureAwait(false);
        var id = draft ?? context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "StandardCostPrepared",
                1,
                Pricing.CostAggregate,
                id,
                await SalesSql.NextEventVersionAsync(context, Pricing.CostAggregate, id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { costVersionId = id, itemId = command.ItemId, valuationAreaId = command.ValuationAreaId, unitCost = Pricing.Money4(unitCost) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            var version = (await SalesSql.ScalarAsync<int?>(
                context, "SELECT max(version) FROM md.standard_cost_version WHERE company_id = @c AND item_id = @i AND valuation_area_id = @a", cancellationToken,
                ("c", context.CompanyId), ("i", command.ItemId), ("a", command.ValuationAreaId)).ConfigureAwait(false) ?? 0) + 1;
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.standard_cost_version (cost_version_id, company_id, item_id, valuation_area_id, version, effective_from, unit_cost, status, prepared_by)
                VALUES (@id, @c, @i, @a, @v, @today, @cost, 'DRAFT', @by)
                """,
                cancellationToken,
                ("id", id),
                ("c", context.CompanyId),
                ("i", command.ItemId),
                ("a", command.ValuationAreaId),
                ("v", version),
                ("today", SalesSql.Today(context)),
                ("cost", unitCost),
                ("by", preparer)).ConfigureAwait(false);
            await context.AppendStateAsync(Pricing.CostAggregate, id, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE md.standard_cost_version SET unit_cost = @cost WHERE cost_version_id = @id", cancellationToken, ("cost", unitCost), ("id", id)).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { costVersionId = id, status = "DRAFT", replaced = draft is not null });
    }
}

[RequiresPermission("standard_cost:approve", StepUp = true)]
public sealed class ApproveStandardCostHandler : ICommandHandler<ApproveStandardCost>
{
    public string CommandType => "Sales.ApproveStandardCost";

    private sealed record Row(Guid ItemId, Guid AreaId, int Version, string Status, Guid PreparedBy, decimal UnitCost);

    public async Task<string> HandleAsync(ApproveStandardCost command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var key = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT item_id, valuation_area_id, version, status, prepared_by, unit_cost FROM md.standard_cost_version WHERE company_id = @c AND cost_version_id = @id",
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetGuid(4), r.GetDecimal(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", command.CostVersionId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The standard cost version does not exist.");
        await SalesSql.LockAsync(context, $"standard-cost:{key.ItemId}:{key.AreaId}", cancellationToken).ConfigureAwait(false);
        var row = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT item_id, valuation_area_id, version, status, prepared_by, unit_cost FROM md.standard_cost_version WHERE cost_version_id = @id",
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetGuid(4), r.GetDecimal(5)),
            cancellationToken,
            ("id", command.CostVersionId)).ConfigureAwait(false))!;
        var approver = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        Pricing.EnsureApprovable(new Pricing.Approval(row.Status, row.PreparedBy), approver, "standard cost");

        var previous = await SalesSql.ScalarAsync<Guid?>(
            context,
            "SELECT cost_version_id FROM md.standard_cost_version WHERE company_id = @c AND item_id = @i AND valuation_area_id = @a AND status = 'ACTIVE'",
            cancellationToken,
            ("c", context.CompanyId),
            ("i", row.ItemId),
            ("a", row.AreaId)).ConfigureAwait(false);
        // E-VS3-02-7: no new cost while the item has stock in the area (revaluation comes later).
        if (previous is not null && await SalesSql.ScalarAsync<decimal?>(
                context, "SELECT quantity FROM inv.inv_valuation_balance WHERE valuation_area_id = @a AND item_id = @i", cancellationToken,
                ("a", row.AreaId), ("i", row.ItemId)).ConfigureAwait(false) is { } quantity && quantity != 0m)
        {
            throw new DomainException(SalesErrors.StockExists, "The item has stock in this valuation area; its standard cost cannot change until revaluation exists (E-VS3-02-7).");
        }

        var today = SalesSql.Today(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "StandardCostApproved",
                1,
                Pricing.CostAggregate,
                command.CostVersionId,
                await SalesSql.NextEventVersionAsync(context, Pricing.CostAggregate, command.CostVersionId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { costVersionId = command.CostVersionId, itemId = row.ItemId, valuationAreaId = row.AreaId, version = row.Version, unitCost = Pricing.Money4(row.UnitCost), effectiveFrom = today, supersedes = previous }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (previous is { } old)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE md.standard_cost_version SET status = 'SUPERSEDED' WHERE cost_version_id = @id", cancellationToken, ("id", old)).ConfigureAwait(false);
            await context.AppendStateAsync(Pricing.CostAggregate, old, "DOCUMENT", "ACTIVE", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.standard_cost_version SET status = 'ACTIVE', approved_by = @by, effective_from = @today WHERE cost_version_id = @id",
            cancellationToken,
            ("by", approver),
            ("today", today),
            ("id", command.CostVersionId)).ConfigureAwait(false);
        await context.AppendStateAsync(Pricing.CostAggregate, command.CostVersionId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { costVersionId = command.CostVersionId, status = "ACTIVE", effectiveFrom = today, superseded = previous });
    }
}

[RequiresPermission("price_list:prepare")]
public sealed class PreparePriceListHandler : ICommandHandler<PreparePriceList>
{
    public string CommandType => "Sales.PreparePriceList";

    public async Task<string> HandleAsync(PreparePriceList command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var lines = command.Lines ?? [];
        if (lines.Count == 0)
        {
            throw new DomainException(SalesErrors.LinesRequired, "A price list has at least one line.");
        }

        var today = SalesSql.Today(context);
        var seen = new HashSet<(Guid, string)>();
        var normalized = new List<(Guid ItemId, string Uom, decimal Price)>();
        foreach (var line in lines)
        {
            var uom = (line.Uom ?? string.Empty).Trim();
            if (!seen.Add((line.ItemId, uom)))
            {
                throw new DomainException(SalesErrors.DuplicateLine, "Each item and unit appears once in a price list.");
            }

            var price = SalesSql.Positive(line.UnitPrice, 4, "The unit price");
            var baseUom = await Pricing.FinishedGoodBaseUomAsync(context, line.ItemId, cancellationToken).ConfigureAwait(false);
            if (uom != baseUom && await SalesSql.ScalarAsync<string>(
                    context,
                    """
                    SELECT from_uom FROM md.uom_conversion
                    WHERE company_id = @c AND item_id = @i AND from_uom = @u AND to_uom = @b AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
                    """,
                    cancellationToken,
                    ("c", context.CompanyId),
                    ("i", line.ItemId),
                    ("u", uom),
                    ("b", baseUom),
                    ("d", today)).ConfigureAwait(false) is null)
            {
                throw new DomainException(SalesErrors.UomNotConvertible, $"Unit {uom} is neither the base unit ({baseUom}) nor has a conversion in force for item {line.ItemId}.");
            }

            normalized.Add((line.ItemId, uom, price));
        }

        await SalesSql.LockAsync(context, "price-list", cancellationToken).ConfigureAwait(false);
        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = (await SalesSql.ScalarAsync<int?>(context, "SELECT max(version) FROM sal.price_list_version WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0) + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PriceListPrepared",
                1,
                Pricing.PriceAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { priceListVersionId = context.ResultRef, version, lines = normalized.Select(l => new { itemId = l.ItemId, uom = l.Uom, unitPrice = Pricing.Money4(l.Price) }) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO sal.price_list_version (price_list_version_id, company_id, version, effective_from, status, prepared_by) VALUES (@id, @c, @v, @today, 'DRAFT', @by)",
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("v", version),
            ("today", today),
            ("by", preparer)).ConfigureAwait(false);
        foreach (var (itemId, uom, price) in normalized)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO sal.price_list_line (price_list_version_id, company_id, item_id, uom, unit_price) VALUES (@id, @c, @i, @u, @p)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("i", itemId),
                ("u", uom),
                ("p", price)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Pricing.PriceAggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { priceListVersionId = context.ResultRef, version, status = "DRAFT", lines = normalized.Count });
    }
}

[RequiresPermission("price_list:approve", StepUp = true)]
public sealed class ApprovePriceListHandler : ICommandHandler<ApprovePriceList>
{
    public string CommandType => "Sales.ApprovePriceList";

    public async Task<string> HandleAsync(ApprovePriceList command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        await SalesSql.LockAsync(context, "price-list", cancellationToken).ConfigureAwait(false);
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, prepared_by FROM sal.price_list_version WHERE company_id = @c AND price_list_version_id = @id",
            r => new Pricing.Approval(r.GetString(0), r.GetGuid(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", command.PriceListVersionId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The price list does not exist.");
        var approver = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        Pricing.EnsureApprovable(row, approver, "price list");
        var previous = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT price_list_version_id FROM sal.price_list_version WHERE company_id = @c AND status = 'ACTIVE'", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var today = SalesSql.Today(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PriceListApproved",
                1,
                Pricing.PriceAggregate,
                command.PriceListVersionId,
                await SalesSql.NextEventVersionAsync(context, Pricing.PriceAggregate, command.PriceListVersionId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { priceListVersionId = command.PriceListVersionId, effectiveFrom = today, supersedes = previous }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (previous is { } old)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE sal.price_list_version SET status = 'SUPERSEDED' WHERE price_list_version_id = @id", cancellationToken, ("id", old)).ConfigureAwait(false);
            await context.AppendStateAsync(Pricing.PriceAggregate, old, "DOCUMENT", "ACTIVE", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE sal.price_list_version SET status = 'ACTIVE', approved_by = @by, effective_from = @today WHERE price_list_version_id = @id",
            cancellationToken,
            ("by", approver),
            ("today", today),
            ("id", command.PriceListVersionId)).ConfigureAwait(false);
        await context.AppendStateAsync(Pricing.PriceAggregate, command.PriceListVersionId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { priceListVersionId = command.PriceListVersionId, status = "ACTIVE", effectiveFrom = today, superseded = previous });
    }
}
