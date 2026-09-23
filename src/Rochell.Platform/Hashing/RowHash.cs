namespace Rochell.Platform.Hashing;

/// <summary>
/// Row hash v1: SHA-256 over canonical fields "ROCHELL-LEDGER-v1", ledger name, then the row's fields
/// (all columns except row_hash, in declared order — erratum E-PR02-2).
/// </summary>
public static class RowHash
{
    public const string Prefix = "ROCHELL-LEDGER-v1";

    public static CanonicalWriter Begin(string ledger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledger);
        return new CanonicalWriter().Text(Prefix).Text(ledger);
    }
}

/// <summary>Column values of core.domain_event, in declared order (row_hash excluded).</summary>
public sealed record DomainEventRow(
    Guid EventId,
    Guid CompanyId,
    Guid CommandId,
    int CommandEventIndex,
    string EventType,
    int SchemaVersion,
    string AggregateType,
    Guid AggregateId,
    long AggregateVersion,
    short EventSequence,
    DateTime OccurredAt,
    DateTime RecordedAt,
    DateOnly BusinessDate,
    Guid SessionId,
    Guid CorrelationId,
    Guid? CausationId,
    string Payload)
{
    public const string Ledger = "DOMAIN_EVENT";

    public byte[] ComputeRowHash() => RowHash.Begin(Ledger)
        .Uuid(EventId)
        .Uuid(CompanyId)
        .Uuid(CommandId)
        .Int32(CommandEventIndex)
        .Text(EventType)
        .Int32(SchemaVersion)
        .Text(AggregateType)
        .Uuid(AggregateId)
        .Int64(AggregateVersion)
        .Int16(EventSequence)
        .Timestamp(OccurredAt)
        .Timestamp(RecordedAt)
        .Date(BusinessDate)
        .Uuid(SessionId)
        .Uuid(CorrelationId)
        .Uuid(CausationId)
        .Json(Payload)
        .Sha256();
}
