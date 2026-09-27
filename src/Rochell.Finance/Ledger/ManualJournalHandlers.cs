using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Finance.Ledger;

internal static class ManualJournals
{
    public static readonly IReadOnlySet<string> Components = new HashSet<string>(StringComparer.Ordinal) { "ACR-NTX", "ACR-TAX" };

    public sealed record Header(DateOnly PostingDate, string Description, string SupportRef, byte[] SupportSha256, string Component, bool AutoReverse);

    public sealed record Row(string JournalNo, DateOnly PostingDate, string Description, string Component, bool AutoReverse, string Status, Guid PreparedBy, Guid? PostingEventId, long Version);

    public static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Validates header and lines (E-FIN1-2/7, E-FIN1-01-4); balance is required only when sent for approval.</summary>
    public static async Task<Header> ValidateAsync(
        CommandContext context, DateOnly postingDate, string? description, string? supportRef, string? supportSha256, string? component, bool autoReverse,
        IReadOnlyList<ManualJournalLine>? lines, CancellationToken cancellationToken)
    {
        var text = (description ?? string.Empty).Trim();
        var reference = (supportRef ?? string.Empty).Trim();
        if (text.Length is 0 or > 500)
        {
            throw new DomainException(LedgerErrors.LinesInvalid, "The description must have 1 to 500 characters.");
        }

        byte[] hash;
        try
        {
            hash = Convert.FromHexString((supportSha256 ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            hash = [];
        }

        if (reference.Length == 0 || hash.Length != 32)
        {
            throw new DomainException(LedgerErrors.SupportInvalid, "An adjustment needs its support: a reference and the SHA-256 of the document (64 hex characters, E-FIN1-7).");
        }

        var c = (component ?? string.Empty).Trim().ToUpperInvariant();
        if (!Components.Contains(c))
        {
            throw new DomainException(LedgerErrors.LinesInvalid, "The close component must be ACR-NTX or ACR-TAX (E-FIN1-6).");
        }

        if (postingDate > BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))
        {
            throw new DomainException(LedgerErrors.DateInvalid, "An adjustment is not dated in the future.");
        }

        if (lines is null || lines.Count < 2)
        {
            throw new DomainException(LedgerErrors.LinesInvalid, "An adjustment has at least two lines.");
        }

        foreach (var l in lines)
        {
            if (l.Debit < 0m || l.Credit < 0m || (l.Debit == 0m) == (l.Credit == 0m) || l.Debit != decimal.Round(l.Debit, 2) || l.Credit != decimal.Round(l.Credit, 2))
            {
                throw new DomainException(LedgerErrors.LinesInvalid, "Each line has either a debit or a credit greater than zero, with at most 2 decimals.");
            }
        }

        var accounts = lines.Select(l => l.AccountId).Distinct().ToArray();
        var allowed = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT account_id FROM fin.account WHERE company_id = @c AND account_id = ANY(@a) AND NOT is_control AND status = 'ACTIVE'",
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("a", accounts)).ConfigureAwait(false);
        if (allowed.Count != accounts.Length)
        {
            throw new DomainException(LedgerErrors.AccountNotAllowed, "Adjustments go only to active accounts that are not control accounts (E-FIN1-2); bank, receivable, payable and inventory accounts move through their documents.");
        }

        return new Header(postingDate, text, reference, hash, c, autoReverse);
    }

    public static async Task WriteLinesAsync(CommandContext context, Guid journalId, long version, IReadOnlyList<ManualJournalLine> lines, CancellationToken cancellationToken)
    {
        var no = 0;
        foreach (var l in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.manual_journal_line (company_id, manual_journal_id, line_no, account_id, debit, credit, plant_id, party_id, memo, journal_version)
                VALUES (@c, @j, @n, @a, @d, @cr, @p, @party, @memo, @v)
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("j", journalId),
                ("n", ++no),
                ("a", l.AccountId),
                ("d", l.Debit),
                ("cr", l.Credit),
                ("p", l.PlantId),
                ("party", l.PartyId),
                ("memo", string.IsNullOrWhiteSpace(l.Memo) ? null : l.Memo.Trim()),
                ("v", version)).ConfigureAwait(false);
        }
    }

    public static async Task<Row> LockAsync(CommandContext context, Guid journalId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT journal_no, posting_date, description, close_component, auto_reverse, status, prepared_by, posting_event_id, version
            FROM fin.manual_journal WHERE company_id = @c AND manual_journal_id = @j FOR UPDATE
            """,
            r => new Row(r.GetString(0), r.Date(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetString(5), r.GetGuid(6), r.NullableGuid(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("j", journalId)).ConfigureAwait(false)
            ?? throw new DomainException(LedgerErrors.NotFound, "The adjustment does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(LedgerErrors.VersionConflict, $"The adjustment is at version {row.Version}, not {expectedVersion}.");
    }

    public static Task<List<PostingEngine.ManualLine>> CurrentLinesAsync(CommandContext context, Guid journalId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT account_id, debit, credit, plant_id, party_id, memo FROM fin.manual_journal_line
            WHERE manual_journal_id = @j AND journal_version = (SELECT max(journal_version) FROM fin.manual_journal_line WHERE manual_journal_id = @j)
            ORDER BY line_no
            """,
            r => new PostingEngine.ManualLine(r.GetGuid(0), r.GetDecimal(1), r.GetDecimal(2), r.NullableGuid(3), r.NullableGuid(4), r.NullableString(5)),
            cancellationToken,
            ("j", journalId));

    public static async Task<Guid> JournalOfAsync(CommandContext context, Guid postingEventId, CancellationToken cancellationToken)
        => (await LedgerSql.ScalarAsync<Guid?>(
               context,
               "SELECT journal_id FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'MANUAL_ADJUSTMENT'",
               cancellationToken,
               ("c", context.CompanyId),
               ("e", postingEventId)).ConfigureAwait(false))
           ?? throw new DomainException(LedgerErrors.InvalidState, "The adjustment has no journal.");

    public static async Task TransitionAsync(
        CommandContext context, Guid journalId, Row row, string to, string eventType, object payload, string commandType, string? reason, CancellationToken cancellationToken,
        string extraSet = "", params (string Name, object? Value)[] extra)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, LedgerSql.JournalAggregate, journalId, version, JsonSerializer.Serialize(payload), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"UPDATE fin.manual_journal SET status = @s, version = @v{extraSet} WHERE manual_journal_id = @id",
            cancellationToken,
            [("s", to), ("v", version), ("id", journalId), .. extra]).ConfigureAwait(false);
        await context.AppendStateAsync(LedgerSql.JournalAggregate, journalId, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
    }
}

[RequiresPermission("manual_journal:prepare")]
public sealed class PrepareManualJournalHandler : ICommandHandler<PrepareManualJournal>
{
    public string CommandType => "Finance.PrepareManualJournal";

    public async Task<string> HandleAsync(PrepareManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var h = await ManualJournals.ValidateAsync(context, command.PostingDate, command.Description, command.SupportRef, command.SupportSha256, command.CloseComponent, command.AutoReverse, command.Lines, cancellationToken).ConfigureAwait(false);
        var preparer = await LedgerSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        // E-FIN1-01-7: AJ-000001… per company, serialized per company.
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('manual-journal-no:' || @c, 0))", cancellationToken, ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var last = await LedgerSql.ScalarAsync<int?>(
            context, "SELECT max(substring(journal_no from 4)::int) FROM fin.manual_journal WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var number = "AJ-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ManualJournalPrepared",
                1,
                LedgerSql.JournalAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    manualJournalId = context.ResultRef,
                    journalNo = number,
                    postingDate = h.PostingDate,
                    description = h.Description,
                    closeComponent = h.Component,
                    autoReverse = h.AutoReverse,
                    lines = command.Lines.Select(l => new { accountId = l.AccountId, debit = ManualJournals.Money(l.Debit), credit = ManualJournals.Money(l.Credit) }),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.manual_journal (manual_journal_id, company_id, journal_no, posting_date, description, support_ref, support_sha256, close_component,
              auto_reverse, status, prepared_by, version)
            VALUES (@id, @c, @no, @date, @desc, @ref, @hash, @comp, @auto, 'DRAFT', @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", number),
            ("date", h.PostingDate),
            ("desc", h.Description),
            ("ref", h.SupportRef),
            ("hash", h.SupportSha256),
            ("comp", h.Component),
            ("auto", h.AutoReverse),
            ("by", preparer)).ConfigureAwait(false);
        await ManualJournals.WriteLinesAsync(context, context.ResultRef, 1, command.Lines, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(LedgerSql.JournalAggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { manualJournalId = context.ResultRef, journalNo = number, status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("manual_journal:prepare")]
public sealed class UpdateManualJournalHandler : ICommandHandler<UpdateManualJournal>
{
    public string CommandType => "Finance.UpdateManualJournal";

    public async Task<string> HandleAsync(UpdateManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ManualJournals.LockAsync(context, command.ManualJournalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(LedgerErrors.InvalidState, $"The adjustment is {row.Status}; only a DRAFT is changed.");
        }

        var h = await ManualJournals.ValidateAsync(context, command.PostingDate, command.Description, command.SupportRef, command.SupportSha256, command.CloseComponent, command.AutoReverse, command.Lines, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "ManualJournalUpdated",
                1,
                LedgerSql.JournalAggregate,
                command.ManualJournalId,
                version,
                JsonSerializer.Serialize(new
                {
                    manualJournalId = command.ManualJournalId,
                    postingDate = h.PostingDate,
                    description = h.Description,
                    closeComponent = h.Component,
                    autoReverse = h.AutoReverse,
                    lines = command.Lines.Select(l => new { accountId = l.AccountId, debit = ManualJournals.Money(l.Debit), credit = ManualJournals.Money(l.Credit) }),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE fin.manual_journal SET posting_date = @date, description = @desc, support_ref = @ref, support_sha256 = @hash, close_component = @comp,
              auto_reverse = @auto, version = @v
            WHERE manual_journal_id = @id
            """,
            cancellationToken,
            ("date", h.PostingDate),
            ("desc", h.Description),
            ("ref", h.SupportRef),
            ("hash", h.SupportSha256),
            ("comp", h.Component),
            ("auto", h.AutoReverse),
            ("v", version),
            ("id", command.ManualJournalId)).ConfigureAwait(false);
        await ManualJournals.WriteLinesAsync(context, command.ManualJournalId, version, command.Lines, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { manualJournalId = command.ManualJournalId, status = "DRAFT", version });
    }
}

[RequiresPermission("manual_journal:prepare")]
public sealed class SubmitManualJournalHandler : ICommandHandler<SubmitManualJournal>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Finance.SubmitManualJournal";

    public async Task<string> HandleAsync(SubmitManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ManualJournals.LockAsync(context, command.ManualJournalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(LedgerErrors.InvalidState, $"The adjustment is {row.Status}.");
        }

        var lines = await ManualJournals.CurrentLinesAsync(context, command.ManualJournalId, cancellationToken).ConfigureAwait(false);
        var debit = lines.Sum(l => l.Debit);
        var credit = lines.Sum(l => l.Credit);
        if (lines.Count < 2 || debit != credit)
        {
            throw new DomainException(LedgerErrors.Unbalanced, $"Debits {ManualJournals.Money(debit)} and credits {ManualJournals.Money(credit)} must be equal.");
        }

        await _engine.ManualPeriodAsync(context, row.Component, row.PostingDate, cancellationToken).ConfigureAwait(false);
        await ManualJournals.TransitionAsync(
            context, command.ManualJournalId, row, "PENDING_APPROVAL", "ManualJournalSubmitted",
            new { manualJournalId = command.ManualJournalId, journalNo = row.JournalNo, total = ManualJournals.Money(debit) }, CommandType, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { manualJournalId = command.ManualJournalId, status = "PENDING_APPROVAL", version = row.Version + 1 });
    }
}

[RequiresPermission("manual_journal:prepare")]
public sealed class WithdrawManualJournalHandler : ICommandHandler<WithdrawManualJournal>
{
    public string CommandType => "Finance.WithdrawManualJournal";

    public async Task<string> HandleAsync(WithdrawManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ManualJournals.LockAsync(context, command.ManualJournalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PENDING_APPROVAL")
        {
            throw new DomainException(LedgerErrors.InvalidState, $"The adjustment is {row.Status}.");
        }

        await ManualJournals.TransitionAsync(
            context, command.ManualJournalId, row, "DRAFT", "ManualJournalWithdrawn", new { manualJournalId = command.ManualJournalId, journalNo = row.JournalNo }, CommandType, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { manualJournalId = command.ManualJournalId, status = "DRAFT", version = row.Version + 1 });
    }
}

[RequiresPermission("manual_journal:approve", StepUp = true)]
public sealed class ApproveManualJournalHandler : ICommandHandler<ApproveManualJournal>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Finance.ApproveManualJournal";

    public async Task<string> HandleAsync(ApproveManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ManualJournals.LockAsync(context, command.ManualJournalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PENDING_APPROVAL")
        {
            throw new DomainException(LedgerErrors.InvalidState, $"The adjustment is {row.Status}.");
        }

        var approver = await LedgerSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy)
        {
            throw new DomainException(LedgerErrors.FourEyes, "An adjustment is approved by someone other than who prepared it (E-FIN1-1).");
        }

        var lines = await ManualJournals.CurrentLinesAsync(context, command.ManualJournalId, cancellationToken).ConfigureAwait(false);
        var periodId = await _engine.ManualPeriodAsync(context, row.Component, row.PostingDate, cancellationToken).ConfigureAwait(false);
        var now = context.Clock.UtcNow;
        var nextMonth = new DateOnly(row.PostingDate.Year, row.PostingDate.Month, 1).AddMonths(1);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ManualJournalApproved",
                1,
                LedgerSql.JournalAggregate,
                command.ManualJournalId,
                version,
                JsonSerializer.Serialize(new
                {
                    manualJournalId = command.ManualJournalId,
                    journalNo = row.JournalNo,
                    postingDate = row.PostingDate,
                    closeComponent = row.Component,
                    autoReverse = row.AutoReverse,
                    reversalDate = row.AutoReverse ? nextMonth : (DateOnly?)null,
                    total = ManualJournals.Money(lines.Sum(l => l.Debit)),
                }),
                Publish: true,
                BusinessDate: row.PostingDate),
            cancellationToken).ConfigureAwait(false);

        // The adjustment names its posting event before its journals are written (the close gate finds the component through it).
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.manual_journal SET status = 'POSTED', approved_by = @by, posting_event_id = @e, reversal_event_id = @r, version = @v WHERE manual_journal_id = @id",
            cancellationToken,
            ("by", approver),
            ("e", eventId),
            ("r", row.AutoReverse ? eventId : null),
            ("v", version),
            ("id", command.ManualJournalId)).ConfigureAwait(false);
        var inputs = new Dictionary<string, object?> { ["manual_journal_id"] = command.ManualJournalId, ["journal_no"] = row.JournalNo, ["close_component"] = row.Component };
        var journal = await _engine.WriteManualAsync(context, periodId, row.PostingDate, lines, eventId, inputs, now, cancellationToken).ConfigureAwait(false);
        PostedJournal? reversal = null;
        if (row.AutoReverse)
        {
            // E-FIN1-01-6: the exact reversal on the first day of the next month, in this same transaction.
            var plan = await _engine.PrepareReversalAsync(context, journal.JournalId, nextMonth, cancellationToken).ConfigureAwait(false);
            reversal = await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false);
        }

        await context.AppendStateAsync(LedgerSql.JournalAggregate, command.ManualJournalId, "DOCUMENT", "PENDING_APPROVAL", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            manualJournalId = command.ManualJournalId,
            status = "POSTED",
            version,
            journalId = journal.JournalId,
            reversalJournalId = reversal?.JournalId,
            reversalDate = reversal?.PostingDate,
        });
    }
}

[RequiresPermission("manual_journal:approve")]
public sealed class RejectManualJournalHandler : ICommandHandler<RejectManualJournal>
{
    public string CommandType => "Finance.RejectManualJournal";

    public async Task<string> HandleAsync(RejectManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(LedgerErrors.ReasonRequired, "Rejecting an adjustment needs a reason.");
        }

        var row = await ManualJournals.LockAsync(context, command.ManualJournalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PENDING_APPROVAL")
        {
            throw new DomainException(LedgerErrors.InvalidState, $"The adjustment is {row.Status}.");
        }

        var rejecter = await LedgerSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (rejecter == row.PreparedBy)
        {
            throw new DomainException(LedgerErrors.FourEyes, "An adjustment is rejected by someone other than who prepared it.");
        }

        await ManualJournals.TransitionAsync(
            context, command.ManualJournalId, row, "REJECTED", "ManualJournalRejected", new { manualJournalId = command.ManualJournalId, journalNo = row.JournalNo, reason },
            CommandType, reason, cancellationToken, ", rejected_by = @by, rejection_reason = @reason", ("by", rejecter), ("reason", reason)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { manualJournalId = command.ManualJournalId, status = "REJECTED", version = row.Version + 1 });
    }
}

[RequiresPermission("manual_journal:approve", StepUp = true)]
public sealed class ReverseManualJournalHandler : ICommandHandler<ReverseManualJournal>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Finance.ReverseManualJournal";

    public async Task<string> HandleAsync(ReverseManualJournal command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(LedgerErrors.ReasonRequired, "Reversing an adjustment needs a reason.");
        }

        var row = await ManualJournals.LockAsync(context, command.ManualJournalId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "POSTED" || row.AutoReverse)
        {
            throw new DomainException(LedgerErrors.InvalidState, row.AutoReverse ? "The adjustment already reverses itself on the first day of the next month." : $"The adjustment is {row.Status}.");
        }

        var journal = await ManualJournals.JournalOfAsync(context, row.PostingEventId!.Value, cancellationToken).ConfigureAwait(false);
        var now = context.Clock.UtcNow;
        var plan = await _engine.PrepareReversalAsync(context, journal, BusinessCalendar.DefaultBusinessDate(now), cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ManualJournalReversed",
                1,
                LedgerSql.JournalAggregate,
                command.ManualJournalId,
                version,
                JsonSerializer.Serialize(new { manualJournalId = command.ManualJournalId, journalNo = row.JournalNo, reason, reversedJournalId = journal, postingDate = plan.PostingDate }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.manual_journal SET status = 'REVERSED', reversal_event_id = @e, version = @v WHERE manual_journal_id = @id",
            cancellationToken,
            ("e", eventId),
            ("v", version),
            ("id", command.ManualJournalId)).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(LedgerSql.JournalAggregate, command.ManualJournalId, "DOCUMENT", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { manualJournalId = command.ManualJournalId, status = "REVERSED", version, reversalJournalId = reversal.JournalId, postingDate = reversal.PostingDate });
    }
}
