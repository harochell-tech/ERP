using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Lots;

/// <summary>
/// E-MFG1-8, E-MFG1-04-1/2: Calidad releases a CURING lot once its minimum curing hours have passed: the whole lot moves from CURADO to
/// a stock location of the plant (a TRANSFER pair without value, no journal).
/// </summary>
public sealed record ReleaseLot(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, long ExpectedVersion, Guid ToLocationId) : IPlantScopedCommand;

/// <summary>E-MFG1-04-3: Calidad blocks a CURING lot (reason required); a blocked lot is never released.</summary>
public sealed record BlockLot(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, long ExpectedVersion, string Reason) : IPlantScopedCommand;

/// <summary>E-MFG1-04-3: Calidad returns a BLOCKED lot to CURING (reason required).</summary>
public sealed record UnblockLot(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, long ExpectedVersion, string Reason) : IPlantScopedCommand;

/// <summary>
/// E-MFG1-13, E-MFG1-04-4: the plant manager scraps a quantity of a lot from one of its locations (step-up, reason): ISSUE at the area's
/// valuation cost + P-12; point CURING from CURADO, YARD otherwise; a lot with no stock left becomes SCRAPPED.
/// </summary>
public sealed record ScrapLot(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, Guid LocationId, decimal Quantity, string Reason) : IPlantScopedCommand;
