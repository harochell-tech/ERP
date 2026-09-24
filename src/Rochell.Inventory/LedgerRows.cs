using Rochell.Platform.Hashing;

namespace Rochell.Inventory;

/// <summary>Columns of inv.inv_quantity_entry in declared order, row_hash excluded (E-PR07-6).</summary>
public sealed record QuantityEntryRow(
    Guid QuantityEntryId,
    Guid CompanyId,
    string MovementType,
    Guid PlantId,
    Guid LocationId,
    Guid ItemId,
    Guid LotId,
    decimal Quantity,
    Guid SourceEventId,
    string SourceDocumentType,
    Guid SourceDocumentId,
    Guid? SourceLineId,
    Guid? ReversesQuantityEntryId,
    DateTime OccurredAt,
    DateTime RecordedAt,
    DateOnly BusinessDate,
    DateOnly PostingDate)
{
    public const string Ledger = "INV_QUANTITY";

    public byte[] ComputeRowHash() => RowHash.Begin(Ledger)
        .Uuid(QuantityEntryId).Uuid(CompanyId).Text(MovementType).Uuid(PlantId).Uuid(LocationId).Uuid(ItemId).Uuid(LotId)
        .Numeric(Quantity, 6).Uuid(SourceEventId).Text(SourceDocumentType).Uuid(SourceDocumentId).Uuid(SourceLineId)
        .Uuid(ReversesQuantityEntryId).Timestamp(OccurredAt).Timestamp(RecordedAt).Date(BusinessDate).Date(PostingDate)
        .Sha256();
}

/// <summary>Columns of inv.inv_value_entry in declared order, row_hash excluded (E-PR07-6).</summary>
public sealed record ValueEntryRow(
    Guid ValueEntryId,
    Guid CompanyId,
    string MovementType,
    Guid ValuationAreaId,
    Guid PlantId,
    Guid ItemId,
    Guid? QuantityEntryId,
    decimal Amount,
    Guid SourceEventId,
    Guid? ReversesValueEntryId,
    DateTime OccurredAt,
    DateTime RecordedAt,
    DateOnly BusinessDate,
    DateOnly PostingDate)
{
    public const string Ledger = "INV_VALUE";

    public byte[] ComputeRowHash() => RowHash.Begin(Ledger)
        .Uuid(ValueEntryId).Uuid(CompanyId).Text(MovementType).Uuid(ValuationAreaId).Uuid(PlantId).Uuid(ItemId)
        .Uuid(QuantityEntryId).Numeric(Amount, 4).Uuid(SourceEventId).Uuid(ReversesValueEntryId)
        .Timestamp(OccurredAt).Timestamp(RecordedAt).Date(BusinessDate).Date(PostingDate)
        .Sha256();
}
