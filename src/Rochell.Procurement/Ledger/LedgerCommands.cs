using Rochell.Platform.Commands;

namespace Rochell.Procurement.Ledger;

/// <summary>
/// T-11 (R-REP, E-PR14-2): corrects a journal posted with a wrong mapping. Reverses generation n exactly and posts generation n + 1
/// of the same event and rule with the rule and mappings in force, same lines, dimensions and amounts, at the event's business date.
/// </summary>
public sealed record RepostEvent(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SourceEventId, string RuleCode, string Reason) : ICommand;

/// <summary>T-12 (R-06, E-PR14-4): removes an orphan value (quantity 0, value ≠ 0) of an area × item, against the policy's account (E-PR14-5).</summary>
public sealed record ApproveValuationResidualAdjustment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ValuationAreaId, Guid ItemId, string Reason) : ICommand;
