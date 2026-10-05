using Rochell.Platform.Commands;

namespace Rochell.Sales.Pricing;

/// <summary>E-VS3-01-13, E-VS3-02-7: the Controller creates or replaces the DRAFT standard cost of a finished good in a valuation area.</summary>
public sealed record PrepareStandardCost(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ItemId, Guid ValuationAreaId, decimal UnitCost) : ICommand;

/// <summary>A standard price of a raw material of the recipe (DOP per base unit, 4 decimals).</summary>
public sealed record MaterialPriceInput(Guid MaterialItemId, decimal StdPrice);

/// <summary>
/// E-MFG1-9, E-MFG1-02-4/5: the Controller prepares a new DRAFT standard cost with its breakdown from an ACTIVE recipe (the finished
/// good and the valuation area of the recipe's machine): a standard price for each material of the recipe and the conversion cost per
/// unit. Standard quantity per unit = quantity per batch ÷ units per batch (6 decimals); material cost = Σ quantity × price (4
/// decimals); unit cost = material + conversion.
/// </summary>
public sealed record PrepareStandardCostFromRecipe(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RecipeVersionId, IReadOnlyList<MaterialPriceInput> MaterialPrices, decimal ConversionCost) : ICommand;

/// <summary>
/// E-VS3-02-7, E-MFG1-10, E-MFG1-02-6/7: the Aprobador de políticas approves it (step-up), in force from the approval date. With stock in
/// the area the value is brought to quantity × new standard (REVAL); refused while units of the item are in transit.
/// </summary>
public sealed record ApproveStandardCost(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CostVersionId) : ICommand;

public sealed record PriceListLine(Guid ItemId, string Uom, decimal UnitPrice);

/// <summary>E-SRV1-10, E-PRS-03-5: the freight of a product to a zone, per unit of the product (its base unit or one convertible to it).</summary>
public sealed record FreightPriceLine(Guid ItemId, string Uom, Guid ZoneId, decimal UnitPrice);

/// <summary>
/// E-VS3-01-14, E-VS3-02-8: the Controller prepares a new DRAFT version of a price list with all its lines (DOP without ITBIS).
/// E-PRS-02-2: of the list <paramref name="PriceListId"/>, or of GENERAL when none is given; its versions are numbered per list.
/// E-PRS-03-3/4: with its freight table in <paramref name="Freight"/>; a version may hold only freight.
/// </summary>
public sealed record PreparePriceList(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<PriceListLine> Lines, Guid? PriceListId = null, IReadOnlyList<FreightPriceLine>? Freight = null) : ICommand;

/// <summary>E-PRC1-5: the Controller creates a named list (code A–Z, 0–9, _; name); it is priced by preparing its first version.</summary>
public sealed record CreatePriceList(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, string Name) : ICommand;

/// <summary>E-PRS-02-1, E-PRS-01-4/5: takes a list out of use (never GENERAL, never one a customer's terms name).</summary>
public sealed record DeactivatePriceList(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PriceListId, long ExpectedVersion) : ICommand;

/// <summary>E-PRS-02-1: puts an inactive list back in use.</summary>
public sealed record ReactivatePriceList(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PriceListId, long ExpectedVersion) : ICommand;

public static class PriceListErrors
{
    public const string CodeUsed = "PRICE_LIST_CODE_USED";
    public const string Inactive = "PRICE_LIST_INACTIVE";
    public const string InUse = "PRICE_LIST_IN_USE";
    public const string General = "PRICE_LIST_GENERAL";
}

/// <summary>E-VS3-02-8: the Aprobador de políticas approves it (step-up); it replaces the list in force from the approval date.</summary>
public sealed record ApprovePriceList(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PriceListVersionId) : ICommand;
