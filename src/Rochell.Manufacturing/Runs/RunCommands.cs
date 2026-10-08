using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Runs;

/// <summary>
/// E-MFG1-03-1: the supervisor starts a run of a finished good on a machine and shift of the plant for a business date. Needs the ACTIVE
/// recipe (product × machine) and the ACTIVE standard cost with breakdown in the plant's valuation area; the run keeps both versions.
/// </summary>
public sealed record StartProductionRun(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid MachineId, Guid ShiftId, DateOnly BusinessDate, Guid ItemId)
    : IPlantScopedCommand;

/// <summary>Only an IN_PROGRESS run without a shift summary is cancelled.</summary>
public sealed record CancelProductionRun(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid RunId, long ExpectedVersion, string Reason) : IPlantScopedCommand;

/// <summary>Real consumption of a material: quantity in <c>Uom</c> (the base unit or one with a conversion in force), from a location of the plant.</summary>
public sealed record ConsumptionInput(Guid MaterialItemId, Guid LocationId, decimal Quantity, string Uom);

/// <summary>
/// E-MFG1-03-2/3: the supervisor records (or replaces) the DRAFT shift summary of an IN_PROGRESS run: batches, good units, mix and fresh
/// scrap (quantities only) and the real consumption of every material of the recipe.
/// </summary>
public sealed record RecordShiftSummary(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid RunId,
    int Batches,
    decimal GoodUnits,
    decimal MixScrapUnits,
    decimal FreshScrapUnits,
    IReadOnlyList<ConsumptionInput> Consumption,
    string? ConsumptionReason = null) : IPlantScopedCommand;

/// <summary>
/// E-MFG1-03-5: the plant manager posts the DRAFT summary (four eyes): PRODUCTION_ISSUE per lot (FIFO by lot code) + P-08, the finished
/// goods lot into CURADO at standard + P-10, racks. <c>ExpectedVersion</c> is the summary's version.
/// </summary>
public sealed record PostShiftSummary(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid RunId, long ExpectedVersion) : IPlantScopedCommand;

/// <summary>
/// E-MFG1-03-9: the plant manager reverses a POSTED summary (step-up, reason) while its lot has not moved and the collector is OPEN:
/// exact reversals; the run goes back to IN_PROGRESS with a new DRAFT summary carrying the same figures.
/// </summary>
public sealed record ReverseShiftSummary(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid RunId, long ExpectedVersion, string Reason) : IPlantScopedCommand;
