using Rochell.Platform.Commands;

namespace Rochell.Procurement.ReceiptCorrections;

/// <summary>
/// T-04 (E-8 §5.4): proposes a quantity correction of one receipt line. <paramref name="DeltaQuantity"/> is in the PO line UOM
/// (positive: more was received than recorded; negative: less). <paramref name="EvidenceReference"/> is the corrected ticket,
/// photo or record reference (E-PR11-1). Under materiality it stays DRAFT; above it starts PENDING_APPROVAL (E-PR11-4).
/// </summary>
public sealed record CreateReceiptCorrection(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid GoodsReceiptId,
    Guid GoodsReceiptLineId,
    decimal DeltaQuantity,
    string Reason,
    string EvidenceReference) : IPlantScopedCommand;

/// <summary>T-05: approves and posts a correction (Controller; re-authentication when it is above materiality).</summary>
public sealed record ApproveReceiptCorrection(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CorrectionId) : ICommand;

/// <summary>Rejects a correction pending approval (§11.3).</summary>
public sealed record RejectReceiptCorrection(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CorrectionId, string Reason) : ICommand;
