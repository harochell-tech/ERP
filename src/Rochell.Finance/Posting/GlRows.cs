using Rochell.Platform.Hashing;

namespace Rochell.Finance.Posting;

/// <summary>Columns of fin.gl_journal in declared order, row_hash excluded (E-PR05-8).</summary>
public sealed record GlJournalRow(
    Guid JournalId,
    Guid CompanyId,
    DateOnly PostingDate,
    Guid PeriodId,
    Guid SourceEventId,
    Guid PostingRuleId,
    int PostingRuleVersion,
    int PostingGeneration,
    string JournalType,
    Guid? ReversesJournalId,
    bool LateEntry,
    DateTime OccurredAt)
{
    public const string Ledger = "GL_JOURNAL";

    public byte[] ComputeRowHash() => RowHash.Begin(Ledger)
        .Uuid(JournalId).Uuid(CompanyId).Date(PostingDate).Uuid(PeriodId).Uuid(SourceEventId).Uuid(PostingRuleId)
        .Int32(PostingRuleVersion).Int32(PostingGeneration).Text(JournalType).Uuid(ReversesJournalId).Boolean(LateEntry)
        .Timestamp(OccurredAt)
        .Sha256();
}

/// <summary>Columns of fin.gl_entry in declared order, row_hash excluded (E-PR05-8).</summary>
public sealed record GlEntryRow(
    Guid GlEntryId,
    Guid JournalId,
    int LineNo,
    Guid CompanyId,
    DateOnly PostingDate,
    Guid AccountId,
    string AccountRole,
    decimal Debit,
    decimal Credit,
    string Currency,
    Guid? PlantId,
    Guid? ItemId,
    Guid? PartyId,
    string? SubledgerType,
    Guid? SubledgerRef,
    Guid? InvValueEntryId,
    Guid SourceEventId,
    string RuleLineCode,
    string DeterminationInputs)
{
    public const string Ledger = "GL_ENTRY";

    public byte[] ComputeRowHash() => RowHash.Begin(Ledger)
        .Uuid(GlEntryId).Uuid(JournalId).Int32(LineNo).Uuid(CompanyId).Date(PostingDate).Uuid(AccountId).Text(AccountRole)
        .Numeric(Debit, 4).Numeric(Credit, 4).Text(Currency).Uuid(PlantId).Uuid(ItemId).Uuid(PartyId).Text(SubledgerType)
        .Uuid(SubledgerRef).Uuid(InvValueEntryId).Uuid(SourceEventId).Text(RuleLineCode).Json(DeterminationInputs)
        .Sha256();
}
