using Rochell.Platform.Commands;

namespace Rochell.Reconciliation;

/// <summary>Runs the given reconciliations (all when null) and stores their findings (E-PR16-9).</summary>
public sealed record RunReconciliation(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<string>? ReconCodes = null) : ICommand;

/// <summary>T-13 + Patch 1 P-2: closes a period component (INV-MOV or AP-REC).</summary>
public sealed record CloseComponent(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PeriodId, string Component) : ICommand;

/// <summary>Patch 1 P-8 / T-14: asks to reopen a CLOSED component; a second approver decides.</summary>
public sealed record RequestReopen(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PeriodId, string Component, string Reason) : ICommand;

public sealed record ApproveReopen(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RequestId) : ICommand;

public sealed record RejectReopen(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RequestId, string Reason) : ICommand;
