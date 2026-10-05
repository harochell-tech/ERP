using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
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

    /// <summary>A finished good's base unit, or a unit with a conversion in force to it (price and freight lines alike).</summary>
    public static async Task EnsureSellingUomAsync(CommandContext context, Guid itemId, string uom, DateOnly today, CancellationToken cancellationToken)
    {
        var baseUom = await FinishedGoodBaseUomAsync(context, itemId, cancellationToken).ConfigureAwait(false);
        if (uom != baseUom && await SalesSql.ScalarAsync<string>(
                context,
                """
                SELECT from_uom FROM md.uom_conversion
                WHERE company_id = @c AND item_id = @i AND from_uom = @u AND to_uom = @b AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("i", itemId),
                ("u", uom),
                ("b", baseUom),
                ("d", today)).ConfigureAwait(false) is null)
        {
            throw new DomainException(SalesErrors.UomNotConvertible, $"Unit {uom} is neither the base unit ({baseUom}) nor has a conversion in force for item {itemId}.");
        }
    }

    public sealed record Approval(string Status, Guid PreparedBy);

    public static async Task EnsureApprovableAsync(CommandContext context, Approval row, Guid approver, string what, CancellationToken cancellationToken)
    {
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The {what} is {row.Status}.");
        }

        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
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
            "SELECT cost_version_id FROM md.standard_cost_version WHERE company_id = @c AND item_id = @i AND valuation_area_id = @a AND status = 'DRAFT' AND material_cost IS NULL ORDER BY version DESC LIMIT 1",
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

[RequiresPermission("standard_cost:prepare")]
public sealed class PrepareStandardCostFromRecipeHandler : ICommandHandler<PrepareStandardCostFromRecipe>
{
    public string CommandType => "Sales.PrepareStandardCostFromRecipe";

    private sealed record RecipeRow(Guid ItemId, Guid AreaId, string Status, decimal UnitsPerBatch);

    private sealed record LineRow(Guid MaterialItemId, decimal QtyPerBatch);

    public async Task<string> HandleAsync(PrepareStandardCostFromRecipe command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var conversion = command.ConversionCost >= 0m && decimal.Round(command.ConversionCost, 4) == command.ConversionCost
            ? command.ConversionCost
            : throw new DomainException(SalesErrors.AmountInvalid, "The conversion cost is zero or more with at most 4 decimals.");
        var recipe = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.item_id, p.valuation_area_id, v.status, v.units_per_batch
            FROM mfg.recipe_version v JOIN md.machine m ON m.machine_id = v.machine_id JOIN md.plant p ON p.plant_id = m.plant_id
            WHERE v.company_id = @c AND v.recipe_version_id = @r
            """,
            r => new RecipeRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", command.RecipeVersionId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The recipe does not exist.");
        if (recipe.Status != "ACTIVE")
        {
            throw new DomainException(SalesErrors.RecipeNotActive, $"The recipe is {recipe.Status}; a standard cost is prepared from the ACTIVE recipe.");
        }

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT material_item_id, qty_per_batch FROM mfg.recipe_line WHERE recipe_version_id = @r",
            r => new LineRow(r.GetGuid(0), r.GetDecimal(1)),
            cancellationToken,
            ("r", command.RecipeVersionId)).ConfigureAwait(false);
        var prices = command.MaterialPrices ?? [];
        if (prices.Select(p => p.MaterialItemId).Distinct().Count() != prices.Count
            || !prices.Select(p => p.MaterialItemId).ToHashSet().SetEquals(lines.Select(l => l.MaterialItemId)))
        {
            throw new DomainException(SalesErrors.MaterialPricesMismatch, "Give one standard price for each material of the recipe, and only for them (E-MFG1-02-5).");
        }

        var materials = lines.Select(l =>
        {
            var price = SalesSql.Positive(prices.Single(p => p.MaterialItemId == l.MaterialItemId).StdPrice, 4, "The standard price");
            var qty = decimal.Round(l.QtyPerBatch / recipe.UnitsPerBatch, 6, MidpointRounding.AwayFromZero);
            return (l.MaterialItemId, Qty: qty, Price: price);
        }).ToList();
        if (materials.Any(m => m.Qty == 0m))
        {
            throw new DomainException(SalesErrors.AmountInvalid, "A material rounds to zero per unit at 6 decimals; review the recipe.");
        }

        var materialCost = decimal.Round(materials.Sum(m => m.Qty * m.Price), 4, MidpointRounding.AwayFromZero);
        var unitCost = materialCost + conversion;
        if (unitCost <= 0m)
        {
            throw new DomainException(SalesErrors.AmountInvalid, "The standard cost must be greater than zero.");
        }

        await SalesSql.LockAsync(context, $"standard-cost:{recipe.ItemId}:{recipe.AreaId}", cancellationToken).ConfigureAwait(false);
        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = (await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(version) FROM md.standard_cost_version WHERE company_id = @c AND item_id = @i AND valuation_area_id = @a", cancellationToken,
            ("c", context.CompanyId), ("i", recipe.ItemId), ("a", recipe.AreaId)).ConfigureAwait(false) ?? 0) + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "StandardCostPrepared",
                1,
                Pricing.CostAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    costVersionId = context.ResultRef,
                    itemId = recipe.ItemId,
                    valuationAreaId = recipe.AreaId,
                    recipeVersionId = command.RecipeVersionId,
                    unitCost = Pricing.Money4(unitCost),
                    materialCost = Pricing.Money4(materialCost),
                    conversionCost = Pricing.Money4(conversion),
                    materials = materials.Select(m => new { materialItemId = m.MaterialItemId, stdQtyPerUnit = m.Qty.ToString(CultureInfo.InvariantCulture), stdPrice = Pricing.Money4(m.Price) }),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO md.standard_cost_version (cost_version_id, company_id, item_id, valuation_area_id, version, effective_from, unit_cost, status, prepared_by, material_cost, conversion_cost)
            VALUES (@id, @c, @i, @a, @v, @today, @cost, 'DRAFT', @by, @material, @conversion)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("i", recipe.ItemId),
            ("a", recipe.AreaId),
            ("v", version),
            ("today", SalesSql.Today(context)),
            ("cost", unitCost),
            ("by", preparer),
            ("material", materialCost),
            ("conversion", conversion)).ConfigureAwait(false);
        foreach (var (material, qty, price) in materials)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO md.standard_cost_material (cost_version_id, company_id, material_item_id, std_qty_per_unit, std_price) VALUES (@id, @c, @m, @q, @p)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("m", material),
                ("q", qty),
                ("p", price)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Pricing.CostAggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            costVersionId = context.ResultRef,
            version,
            status = "DRAFT",
            unitCost = Pricing.Money4(unitCost),
            materialCost = Pricing.Money4(materialCost),
            conversionCost = Pricing.Money4(conversion),
        });
    }
}

[RequiresPermission("standard_cost:approve", StepUp = true)]
public sealed class ApproveStandardCostHandler : ICommandHandler<ApproveStandardCost>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

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
        await Pricing.EnsureApprovableAsync(context, new Pricing.Approval(row.Status, row.PreparedBy), approver, "standard cost", cancellationToken).ConfigureAwait(false);

        var previous = await SalesSql.ScalarAsync<Guid?>(
            context,
            "SELECT cost_version_id FROM md.standard_cost_version WHERE company_id = @c AND item_id = @i AND valuation_area_id = @a AND status = 'ACTIVE'",
            cancellationToken,
            ("c", context.CompanyId),
            ("i", row.ItemId),
            ("a", row.AreaId)).ConfigureAwait(false);
        // E-MFG1-02-7: the value of units in transit is in FINISHED_GOODS_IN_TRANSIT; revalue only when none is left.
        var plantId = (await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT plant_id FROM md.plant WHERE company_id = @c AND valuation_area_id = @a", cancellationToken, ("c", context.CompanyId), ("a", row.AreaId)).ConfigureAwait(false))!.Value;
        if (await SalesSql.ScalarAsync<decimal?>(
                context,
                "SELECT sum(b.quantity) FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id WHERE b.plant_id = @p AND b.item_id = @i AND l.is_transit",
                cancellationToken,
                ("p", plantId),
                ("i", row.ItemId)).ConfigureAwait(false) is { } inTransit && inTransit != 0m)
        {
            throw new DomainException(SalesErrors.InTransitExists, "Units of the item are in transit; approve the standard cost once they are delivered or returned (E-MFG1-02-7).");
        }

        // E-MFG1-10, E-MFG1-02-6: the area's value becomes quantity × new standard (2 decimals) in the same transaction.
        var (quantity, value) = await _inventory.LockValuationAsync(context, row.AreaId, row.ItemId, cancellationToken).ConfigureAwait(false);
        var revaluation = quantity == 0m ? 0m : decimal.Round(quantity * row.UnitCost, 2, MidpointRounding.AwayFromZero) - value;
        var previousCost = previous is { } p0
            ? await SalesSql.ScalarAsync<decimal>(context, "SELECT unit_cost FROM md.standard_cost_version WHERE cost_version_id = @id", cancellationToken, ("id", p0)).ConfigureAwait(false)
            : 0m;
        PostingPlan? plan = null;
        var valueEntryId = context.Ids.NewId();
        var occurredAt = context.Clock.UtcNow;
        if (revaluation != 0m)
        {
            var inputs = new Dictionary<string, string>
            {
                ["new_unit_cost"] = row.UnitCost.ToString(CultureInfo.InvariantCulture),
                ["previous_unit_cost"] = previousCost.ToString(CultureInfo.InvariantCulture),
                ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
                ["previous_value"] = value.ToString(CultureInfo.InvariantCulture),
            };
            var amount = Math.Abs(revaluation);
            var up = revaluation > 0m;
            plan = await _engine.PrepareAsync(
                context,
                new PostingRequest(
                    "REVAL",
                    SalesSql.Today(context),
                    occurredAt,
                    [
                        new PostingLineInput(up ? "REVAL-UP-DR-FG" : "REVAL-DN-CR-FG", "revaluation", amount, PlantId: plantId, ItemId: row.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId, Inputs: inputs),
                        new PostingLineInput(up ? "REVAL-UP-CR-REV" : "REVAL-DN-DR-REV", "revaluation", amount, PlantId: plantId, Inputs: inputs),
                    ]),
                cancellationToken).ConfigureAwait(false);
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
        Guid? journalId = null;
        if (plan is not null)
        {
            var revalued = await context.AppendEventAsync(
                new EventDraft(
                    "StandardCostRevalued",
                    1,
                    Pricing.CostAggregate,
                    command.CostVersionId,
                    await SalesSql.NextEventVersionAsync(context, Pricing.CostAggregate, command.CostVersionId, cancellationToken).ConfigureAwait(false),
                    JsonSerializer.Serialize(new
                    {
                        costVersionId = command.CostVersionId,
                        itemId = row.ItemId,
                        valuationAreaId = row.AreaId,
                        quantity = quantity.ToString(CultureInfo.InvariantCulture),
                        previousValue = value.ToString(CultureInfo.InvariantCulture),
                        revaluation = revaluation.ToString(CultureInfo.InvariantCulture),
                    }),
                    Publish: true,
                    OccurredAt: occurredAt,
                    BusinessDate: today),
                cancellationToken).ConfigureAwait(false);
            await _inventory.PostValueAdjustmentAsync(
                context, MovementTypes.ValuationAdjustment, row.AreaId, plantId, row.ItemId, revaluation, valueEntryId, null,
                new MovementSource(revalued, "STANDARD_COST", command.CostVersionId), new MovementDates(occurredAt, today, plan.PostingDate), cancellationToken).ConfigureAwait(false);
            journalId = (await _engine.WriteAsync(context, plan, revalued, cancellationToken).ConfigureAwait(false)).JournalId;
        }

        return JsonSerializer.Serialize(new
        {
            costVersionId = command.CostVersionId,
            status = "ACTIVE",
            effectiveFrom = today,
            superseded = previous,
            revaluation = revaluation.ToString("0.00", CultureInfo.InvariantCulture),
            journalId,
        });
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
        var freightLines = command.Freight ?? [];
        if (lines.Count == 0 && freightLines.Count == 0)
        {
            throw new DomainException(SalesErrors.LinesRequired, "A price list has at least one product or freight price (E-PRS-03-4).");
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
            await Pricing.EnsureSellingUomAsync(context, line.ItemId, uom, today, cancellationToken).ConfigureAwait(false);
            normalized.Add((line.ItemId, uom, price));
        }

        var freightSeen = new HashSet<(Guid, string, Guid)>();
        var freight = new List<(Guid ItemId, string Uom, Guid ZoneId, decimal Price)>();
        foreach (var line in freightLines)
        {
            var uom = (line.Uom ?? string.Empty).Trim();
            if (!freightSeen.Add((line.ItemId, uom, line.ZoneId)))
            {
                throw new DomainException(SalesErrors.DuplicateLine, "Each product, unit and zone has one freight price in a version.");
            }

            var price = SalesSql.Positive(line.UnitPrice, 4, "The freight price");
            await Pricing.EnsureSellingUomAsync(context, line.ItemId, uom, today, cancellationToken).ConfigureAwait(false);
            if (await SalesSql.ScalarAsync<string>(context, "SELECT status FROM sal.delivery_zone WHERE company_id = @c AND zone_id = @z", cancellationToken,
                    ("c", context.CompanyId), ("z", line.ZoneId)).ConfigureAwait(false) is not { } zoneStatus)
            {
                throw new DomainException(SalesErrors.NotFound, $"Zone {line.ZoneId} does not exist.");
            }

            if (zoneStatus != "ACTIVE")
            {
                throw new DomainException(Zones.ZoneErrors.Inactive, $"Zone {line.ZoneId} is inactive.");
            }

            freight.Add((line.ItemId, uom, line.ZoneId, price));
        }

        await SalesSql.LockAsync(context, "price-list", cancellationToken).ConfigureAwait(false);
        var listId = await PriceLists.ActiveOrGeneralAsync(context, command.PriceListId, cancellationToken).ConfigureAwait(false);
        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = (await SalesSql.ScalarAsync<int?>(context, "SELECT max(version) FROM sal.price_list_version WHERE price_list_id = @l", cancellationToken, ("l", listId)).ConfigureAwait(false) ?? 0) + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PriceListPrepared",
                1,
                Pricing.PriceAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    priceListVersionId = context.ResultRef,
                    priceListId = listId,
                    version,
                    lines = normalized.Select(l => new { itemId = l.ItemId, uom = l.Uom, unitPrice = Pricing.Money4(l.Price) }),
                    freight = freight.Select(f => new { itemId = f.ItemId, uom = f.Uom, zoneId = f.ZoneId, unitPrice = Pricing.Money4(f.Price) }),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO sal.price_list_version (price_list_version_id, company_id, price_list_id, version, effective_from, status, prepared_by) VALUES (@id, @c, @l, @v, @today, 'DRAFT', @by)",
            cancellationToken,
            ("id", context.ResultRef),
            ("l", listId),
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

        foreach (var (itemId, uom, zoneId, price) in freight)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO sal.price_list_freight (price_list_version_id, company_id, item_id, uom, zone_id, unit_price) VALUES (@id, @c, @i, @u, @z, @p)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("i", itemId),
                ("u", uom),
                ("z", zoneId),
                ("p", price)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Pricing.PriceAggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { priceListVersionId = context.ResultRef, priceListId = listId, version, status = "DRAFT", lines = normalized.Count, freight = freight.Count });
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
        await Pricing.EnsureApprovableAsync(context, row, approver, "price list", cancellationToken).ConfigureAwait(false);
        // E-PRS-02-2: it replaces only the version in force of its own list.
        var previous = await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT a.price_list_version_id FROM sal.price_list_version a JOIN sal.price_list_version d ON d.price_list_id = a.price_list_id
            WHERE d.price_list_version_id = @id AND a.status = 'ACTIVE'
            """,
            cancellationToken,
            ("id", command.PriceListVersionId)).ConfigureAwait(false);
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

/// <summary>E-PRC1-1/5: the named lists' header rows (GENERAL is the migration's, E-PRC1-4).</summary>
internal static class PriceLists
{
    public const string Aggregate = "PriceListHeader";

    public sealed record Row(Guid Id, string Code, string Status, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid priceListId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT price_list_id, code, status, version FROM sal.price_list WHERE company_id = @c AND price_list_id = @id FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", priceListId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The price list does not exist.");
        if (expectedVersion is { } expected && expected != row.Version)
        {
            throw new DomainException(SalesErrors.VersionConflict, $"The price list is at version {row.Version}, not {expected}.");
        }

        return row;
    }

    /// <summary>The list a version or customer terms name: the given one, which must be ACTIVE, or GENERAL.</summary>
    public static async Task<Guid> ActiveOrGeneralAsync(CommandContext context, Guid? priceListId, CancellationToken cancellationToken)
    {
        if (priceListId is not { } id)
        {
            return await SalesSql.ScalarAsync<Guid>(context, "SELECT sal.general_price_list(@c)", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        }

        var row = await LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
        return row.Status == "ACTIVE" ? row.Id : throw new DomainException(PriceListErrors.Inactive, $"The price list {row.Code} is inactive.");
    }

    public static async Task<string> TransitionAsync(
        CommandContext context, Row row, string to, string eventType, string commandType, CancellationToken cancellationToken)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.Id, await SalesSql.NextEventVersionAsync(context, Aggregate, row.Id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { priceListId = row.Id, code = row.Code, status = to }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.price_list SET status = @s, version = @v WHERE price_list_id = @id", cancellationToken,
            ("s", to), ("v", version), ("id", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { priceListId = row.Id, code = row.Code, status = to, version });
    }
}

[RequiresPermission("price_list:prepare")]
public sealed class CreatePriceListHandler : ICommandHandler<CreatePriceList>
{
    public string CommandType => "Sales.CreatePriceList";

    public async Task<string> HandleAsync(CreatePriceList command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9_]{1,29}$"))
        {
            throw new DomainException(SalesErrors.FieldInvalid, "The code has 2 to 30 capitals, digits or underscores.");
        }

        var name = SalesSql.Optional(command.Name, 80, "The name") ?? throw new DomainException(SalesErrors.FieldRequired, "The list needs a name.");
        await SalesSql.LockAsync(context, "price-list", cancellationToken).ConfigureAwait(false);
        await SalesSql.ScalarAsync<Guid>(context, "SELECT sal.general_price_list(@c)", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        if (await SalesSql.ScalarAsync<Guid?>(context, "SELECT price_list_id FROM sal.price_list WHERE company_id = @c AND code = @code", cancellationToken,
                ("c", context.CompanyId), ("code", code)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(PriceListErrors.CodeUsed, $"There is already a price list {code}.");
        }

        var by = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("PriceListCreated", 1, PriceLists.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { priceListId = context.ResultRef, code, name }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO sal.price_list (price_list_id, company_id, code, name, status, created_by, version) VALUES (@id, @c, @code, @name, 'ACTIVE', @by, 1)",
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("code", code),
            ("name", name),
            ("by", by)).ConfigureAwait(false);
        await context.AppendStateAsync(PriceLists.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { priceListId = context.ResultRef, code, name, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("price_list:prepare")]
public sealed class DeactivatePriceListHandler : ICommandHandler<DeactivatePriceList>
{
    public string CommandType => "Sales.DeactivatePriceList";

    public async Task<string> HandleAsync(DeactivatePriceList command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await PriceLists.LockAsync(context, command.PriceListId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Code == "GENERAL")
        {
            throw new DomainException(PriceListErrors.General, "GENERAL is always in use (E-PRS-01-4).");
        }

        if (row.Status != "ACTIVE")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The price list is {row.Status}.");
        }

        var customers = await SalesSql.ScalarAsync<long>(
            context,
            "SELECT count(DISTINCT party_id) FROM sal.customer_terms_version WHERE company_id = @c AND price_list_id = @id AND status IN ('ACTIVE', 'DRAFT')",
            cancellationToken,
            ("c", context.CompanyId),
            ("id", row.Id)).ConfigureAwait(false);
        if (customers > 0)
        {
            throw new DomainException(PriceListErrors.InUse, $"{customers} customer(s) have {row.Code} in their terms in force or pending (E-PRS-01-5).");
        }

        return await PriceLists.TransitionAsync(context, row, "INACTIVE", "PriceListDeactivated", CommandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("price_list:prepare")]
public sealed class ReactivatePriceListHandler : ICommandHandler<ReactivatePriceList>
{
    public string CommandType => "Sales.ReactivatePriceList";

    public async Task<string> HandleAsync(ReactivatePriceList command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await PriceLists.LockAsync(context, command.PriceListId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "INACTIVE")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The price list is {row.Status}.");
        }

        return await PriceLists.TransitionAsync(context, row, "ACTIVE", "PriceListReactivated", CommandType, cancellationToken).ConfigureAwait(false);
    }
}
