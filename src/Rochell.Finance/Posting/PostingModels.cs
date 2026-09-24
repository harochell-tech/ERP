namespace Rochell.Finance.Posting;

/// <summary>
/// One instance of a rule line. <paramref name="AmountSource"/> must equal the rule line's "amount" name (guards mapper bugs).
/// Dimensions must match exactly the rule line's declared dimensions; <paramref name="SubledgerRef"/> is required iff the line has a subledger.
/// </summary>
public sealed record PostingLineInput(
    string LineCode,
    string AmountSource,
    decimal Amount,
    Guid? PlantId = null,
    Guid? ItemId = null,
    Guid? PartyId = null,
    Guid? SubledgerRef = null,
    Guid? InvValueEntryId = null,
    IReadOnlyDictionary<string, string>? Inputs = null);

/// <summary>A posting to prepare (validate) before any write, and to write afterwards with the source event id (Patch 1 §5.2 steps 7 and 11).</summary>
public sealed record PostingRequest(string RuleCode, DateOnly BusinessDate, DateTime OccurredAt, IReadOnlyList<PostingLineInput> Lines, int Generation = 1);

/// <summary>Result of the preflight: everything resolved, nothing written.</summary>
public sealed record PostingPlan(
    PostingRequest Request,
    Guid PostingRuleId,
    int PostingRuleVersion,
    string EventType,
    string CloseComponent,
    Guid PeriodId,
    DateOnly PostingDate,
    bool LateEntry,
    IReadOnlyList<PlannedLine> Lines,
    Guid? RoundingPolicyVersionId = null);

public sealed record PlannedLine(PostingLineInput Input, RuleLine Rule, Guid AccountId, Guid AccountRoleMapId, string? ItemCategory, decimal Debit, decimal Credit);

public sealed record PostedJournal(Guid JournalId, DateOnly PostingDate, bool LateEntry, IReadOnlyList<Guid> EntryIds);
