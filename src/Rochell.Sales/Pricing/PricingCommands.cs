using Rochell.Platform.Commands;

namespace Rochell.Sales.Pricing;

/// <summary>E-VS3-01-13, E-VS3-02-7: the Controller creates or replaces the DRAFT standard cost of a finished good in a valuation area.</summary>
public sealed record PrepareStandardCost(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ItemId, Guid ValuationAreaId, decimal UnitCost) : ICommand;

/// <summary>E-VS3-02-7: the Aprobador de políticas approves it (step-up), in force from the approval date; refused while the item has stock in the area.</summary>
public sealed record ApproveStandardCost(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CostVersionId) : ICommand;

public sealed record PriceListLine(Guid ItemId, string Uom, decimal UnitPrice);

/// <summary>E-VS3-01-14, E-VS3-02-8: the Controller prepares a new DRAFT price list with all its lines (DOP without ITBIS).</summary>
public sealed record PreparePriceList(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<PriceListLine> Lines) : ICommand;

/// <summary>E-VS3-02-8: the Aprobador de políticas approves it (step-up); it replaces the list in force from the approval date.</summary>
public sealed record ApprovePriceList(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PriceListVersionId) : ICommand;
