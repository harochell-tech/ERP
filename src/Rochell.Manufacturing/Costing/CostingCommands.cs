using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Costing;

/// <summary>
/// E-MFG1-11/12, E-MFG1-05-1…3: the Controller settles an OPEN collector after its month ended, with no run IN_PROGRESS: usage variance
/// (Σ per summary and material of (real − standard per unit × good units) × standard price, 2 decimals) and price variance (WIP balance −
/// usage) by P-13 on the month's last day; the collector's WIP ends at zero and it stays SETTLED.
/// </summary>
public sealed record SettleCostCollector(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid CollectorId, long ExpectedVersion) : IPlantScopedCommand;
