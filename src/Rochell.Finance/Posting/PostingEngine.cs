using System.Data.Common;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;

namespace Rochell.Finance.Posting;

/// <summary>
/// Posting Engine (baseline §13, Patch 1 P-1/P-4, E-PR05-2…7). Runs inside the command transaction.
/// <see cref="PrepareAsync"/> validates every prerequisite without writing (step 7 of the template);
/// <see cref="WriteAsync"/> writes the journal, its lines and the balance projection (step 11).
/// Any missing prerequisite throws <see cref="DomainException"/> with <see cref="FinanceErrors.PostingPrerequisiteMissing"/>,
/// so the whole command rolls back. A rounding difference within the POSTING policy tolerance is booked to
/// ROUNDING_DIFFERENCE (rule line R-08); anything larger is rejected (E-PR05-7, PR-06).
/// </summary>
public sealed class PostingEngine
{
    public const string Currency = "DOP";
    public const string RoundingLineCode = "R-08";
    public const string RoundingRole = "ROUNDING_DIFFERENCE";

    public async Task<PostingPlan> PrepareAsync(CommandContext context, PostingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.JournalType is not ("AUTO" or "VALUATION_REALLOCATION"))
        {
            throw new InvalidOperationException("Planned journals are AUTO or VALUATION_REALLOCATION; reversals use ReverseAsync.");
        }

        var rule = await ActiveRuleAsync(context, request.RuleCode, request.BusinessDate, cancellationToken).ConfigureAwait(false);
        var (periodId, postingDate, lateEntry) = await ResolvePostingDateAsync(context, rule.CloseComponent, request.BusinessDate, cancellationToken).ConfigureAwait(false);

        var lines = new List<PlannedLine>();
        foreach (var input in request.Lines)
        {
            var ruleLine = rule.Definition.Line(input.LineCode);
            ValidateShape(ruleLine, input);
            var amount = decimal.Round(input.Amount, 2, MidpointRounding.AwayFromZero);
            if (amount == 0)
            {
                continue; // zero lines are not written
            }

            var category = input.ItemId is null ? null : await ItemCategoryAsync(context, input.ItemId.Value, cancellationToken).ConfigureAwait(false);
            var (accountId, mapId) = await ResolveAccountAsync(context, ruleLine.AccountRole, category, postingDate, cancellationToken).ConfigureAwait(false);
            lines.Add(new PlannedLine(input, ruleLine, accountId, mapId, category, ruleLine.IsDebit ? amount : 0, ruleLine.IsDebit ? 0 : amount));
        }

        var debit = lines.Sum(l => l.Debit);
        var credit = lines.Sum(l => l.Credit);
        Guid? roundingPolicy = null;
        if (lines.Count >= 2 && debit != credit)
        {
            // Only a posting that actually needs rounding depends on the POSTING policy (E-PR06-7).
            var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Posting, request.BusinessDate, cancellationToken).ConfigureAwait(false);
            var difference = debit - credit;
            if (Math.Abs(difference) > policy.Decimal(PolicyParameters.RoundingDifferenceTolerance))
            {
                throw new DomainException(FinanceErrors.PostingUnbalanced, $"Posting {request.RuleCode} differs by {difference} (debit {debit}, credit {credit}), above the rounding tolerance.");
            }

            var side = difference > 0 ? RuleDefinition.Credit : RuleDefinition.Debit;
            var roundingRule = new RuleLine(RoundingLineCode, side, RoundingRole, RoundingLineCode, [], null);
            var (accountId, mapId) = await ResolveAccountAsync(context, RoundingRole, null, postingDate, cancellationToken).ConfigureAwait(false);
            var amount = Math.Abs(difference);
            lines.Add(new PlannedLine(new PostingLineInput(RoundingLineCode, RoundingLineCode, amount), roundingRule, accountId, mapId, null, side == RuleDefinition.Debit ? amount : 0, side == RuleDefinition.Credit ? amount : 0));
            roundingPolicy = policy.PolicyVersionId;
            debit = lines.Sum(l => l.Debit);
            credit = lines.Sum(l => l.Credit);
        }

        if (lines.Count < 2 || debit != credit)
        {
            throw new DomainException(FinanceErrors.PostingUnbalanced, $"Posting {request.RuleCode} does not balance after rounding (debit {debit}, credit {credit}).");
        }

        return new PostingPlan(request, rule.RuleId, rule.Version, rule.EventType, rule.CloseComponent, periodId, postingDate, lateEntry, lines, roundingPolicy);
    }

    public async Task<PostedJournal> WriteAsync(CommandContext context, PostingPlan plan, Guid sourceEventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        await EnsureEventTypeAsync(context, sourceEventId, plan.EventType, cancellationToken).ConfigureAwait(false);

        var journal = new GlJournalRow(
            context.Ids.NewId(), context.CompanyId, plan.PostingDate, plan.PeriodId, sourceEventId, plan.PostingRuleId, plan.PostingRuleVersion,
            plan.Request.Generation, plan.Request.JournalType, null, plan.LateEntry, Platform.Time.Precision.ToMicroseconds(plan.Request.OccurredAt));
        await InsertJournalAsync(context, journal, cancellationToken).ConfigureAwait(false);

        var entries = new List<GlEntryRow>();
        var lineNo = 0;
        foreach (var line in plan.Lines)
        {
            var inputs = new Dictionary<string, object?>
            {
                ["rule"] = plan.Request.RuleCode,
                ["rule_version"] = plan.PostingRuleVersion,
                ["account_role_map_id"] = line.AccountRoleMapId,
                ["item_category"] = line.ItemCategory,
                ["late_entry"] = plan.LateEntry,
                ["business_date"] = plan.Request.BusinessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["inputs"] = line.Input.Inputs ?? new Dictionary<string, string>(),
            };
            if (line.Rule.Code == RoundingLineCode)
            {
                inputs["policy_version_id"] = plan.RoundingPolicyVersionId;
            }

            entries.Add(new GlEntryRow(
                context.Ids.NewId(), journal.JournalId, ++lineNo, context.CompanyId, plan.PostingDate, line.AccountId, line.Rule.AccountRole,
                line.Debit, line.Credit, Currency, line.Input.PlantId, line.Input.ItemId, line.Input.PartyId, line.Rule.Subledger,
                line.Input.SubledgerRef, line.Input.InvValueEntryId, sourceEventId, line.Rule.Code,
                JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(inputs))));
        }

        await WriteEntriesAsync(context, plan.PeriodId, entries, cancellationToken).ConfigureAwait(false);
        return new PostedJournal(journal.JournalId, plan.PostingDate, plan.LateEntry, entries.Select(e => e.GlEntryId).ToList());
    }

    /// <summary>
    /// Patch 1 P-4: the exact mathematical inverse of <paramref name="originalJournalId"/> — same accounts, dimensions and
    /// rule lines, debit and credit swapped, never recalculated. Lines that referenced an inventory value entry must be
    /// mapped to the new inverse value entry through <paramref name="valueEntryMap"/> (original value entry → new one).
    /// </summary>
    public async Task<PostedJournal> ReverseAsync(
        CommandContext context,
        Guid originalJournalId,
        Guid sourceEventId,
        DateOnly businessDate,
        DateTime occurredAt,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, Guid>? valueEntryMap = null)
    {
        var plan = await PrepareReversalAsync(context, originalJournalId, businessDate, cancellationToken).ConfigureAwait(false);
        return await WriteReversalAsync(context, plan, sourceEventId, occurredAt, cancellationToken, valueEntryMap).ConfigureAwait(false);
    }

    /// <summary>Validates an exact reversal before any write (journal exists, not yet reversed, open period) and resolves its posting date.</summary>
    public async Task<ReversalPlan> PrepareReversalAsync(CommandContext context, Guid originalJournalId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var original = await ReadJournalHeaderAsync(context, originalJournalId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(FinanceErrors.JournalNotFound, "The journal to reverse does not exist.");
        if (await IsReversedAsync(context, originalJournalId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FinanceErrors.AlreadyReversed, "The journal has already been reversed.");
        }

        var component = await ScalarAsync<string>(
            context,
            "SELECT close_component FROM fin.posting_rule_version WHERE posting_rule_id = @r AND version = @v",
            cancellationToken,
            ("r", original.RuleId),
            ("v", original.RuleVersion));
        var (periodId, postingDate, lateEntry) = await ResolvePostingDateAsync(context, component!, businessDate, cancellationToken).ConfigureAwait(false);
        return new ReversalPlan(originalJournalId, original.RuleId, original.RuleVersion, periodId, postingDate, lateEntry, businessDate);
    }

    /// <summary>Writes a prepared exact reversal.</summary>
    public async Task<PostedJournal> WriteReversalAsync(
        CommandContext context,
        ReversalPlan plan,
        Guid sourceEventId,
        DateTime occurredAt,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, Guid>? valueEntryMap = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        var journal = new GlJournalRow(
            context.Ids.NewId(), context.CompanyId, plan.PostingDate, plan.PeriodId, sourceEventId, plan.PostingRuleId, plan.PostingRuleVersion,
            1, "REVERSAL", plan.OriginalJournalId, plan.LateEntry, Platform.Time.Precision.ToMicroseconds(occurredAt));
        await InsertJournalAsync(context, journal, cancellationToken).ConfigureAwait(false);

        var originalEntries = await ReadEntriesAsync(context, plan.OriginalJournalId, cancellationToken).ConfigureAwait(false);
        var inverse = new Dictionary<Guid, Guid>();
        foreach (var e in originalEntries.Where(e => e.InvValueEntryId is not null))
        {
            inverse[e.GlEntryId] = await InverseValueEntryAsync(context, e, valueEntryMap, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Entry {e.GlEntryId} references an inventory value entry; its inverse value entry must be supplied (Patch 1 P-1).");
        }

        var entries = originalEntries.Select(e =>
        {
            Guid? valueEntry = e.InvValueEntryId is null ? null : inverse[e.GlEntryId];

            return e with
            {
                GlEntryId = context.Ids.NewId(),
                JournalId = journal.JournalId,
                PostingDate = plan.PostingDate,
                Debit = e.Credit,
                Credit = e.Debit,
                SubledgerRef = valueEntry ?? e.SubledgerRef,
                InvValueEntryId = valueEntry,
                SourceEventId = sourceEventId,
                DeterminationInputs = JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(new { reverses_entry_id = e.GlEntryId, reverses_journal_id = plan.OriginalJournalId, late_entry = plan.LateEntry })),
            };
        }).ToList();

        await WriteEntriesAsync(context, plan.PeriodId, entries, cancellationToken).ConfigureAwait(false);
        return new PostedJournal(journal.JournalId, plan.PostingDate, plan.LateEntry, entries.Select(e => e.GlEntryId).ToList());
    }

    /// <summary>The key under which a repost (R-REP, E-PR14-3) records, per inventory line, the value entry it replaces.</summary>
    public const string RepostsValueEntryInput = "reposts_value_entry_id";

    /// <summary>
    /// The inverse value entry of an inventory line. Callers map the document's original value entries; after one or more reposts
    /// the line references a REPOST value entry instead, so the chain recorded in <see cref="RepostsValueEntryInput"/> is followed
    /// back to the value entry the caller knows.
    /// </summary>
    private static async Task<Guid?> InverseValueEntryAsync(CommandContext context, GlEntryRow entry, IReadOnlyDictionary<Guid, Guid>? map, CancellationToken cancellationToken)
    {
        if (map is null)
        {
            return null;
        }

        var current = entry.InvValueEntryId!.Value;
        var inputs = entry.DeterminationInputs;
        for (var hop = 0; hop < 64; hop++)
        {
            if (map.TryGetValue(current, out var mapped))
            {
                return mapped;
            }

            var replaced = RepostedValueEntry(inputs);
            if (replaced is null)
            {
                return null;
            }

            current = replaced.Value;
            inputs = await ScalarAsync<string>(
                context,
                "SELECT determination_inputs::text FROM fin.gl_entry WHERE company_id = @c AND inv_value_entry_id = @v",
                cancellationToken,
                ("c", context.CompanyId),
                ("v", current)) ?? string.Empty;
        }

        return null;
    }

    private static Guid? RepostedValueEntry(string determinationInputs)
    {
        if (string.IsNullOrEmpty(determinationInputs))
        {
            return null;
        }

        using var document = JsonDocument.Parse(determinationInputs);
        return document.RootElement.TryGetProperty("inputs", out var inputs)
               && inputs.ValueKind == JsonValueKind.Object
               && inputs.TryGetProperty(RepostsValueEntryInput, out var value)
               && Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;
    }

    private static void ValidateShape(RuleLine rule, PostingLineInput input)
    {
        if (input.AmountSource != rule.Amount)
        {
            throw new InvalidOperationException($"Line {rule.Code} expects amount '{rule.Amount}', got '{input.AmountSource}'.");
        }

        if (input.Amount < 0)
        {
            throw new InvalidOperationException($"Line {rule.Code}: amounts are never negative; use the opposite rule line.");
        }

        var provided = new List<string>();
        if (input.PlantId is not null)
        {
            provided.Add("plant");
        }

        if (input.ItemId is not null)
        {
            provided.Add("item");
        }

        if (input.PartyId is not null)
        {
            provided.Add("party");
        }

        if (!provided.Order(StringComparer.Ordinal).SequenceEqual(rule.Dimensions.Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException($"Line {rule.Code} requires dimensions [{string.Join(",", rule.Dimensions)}], got [{string.Join(",", provided)}].");
        }

        if ((rule.Subledger is null) != (input.SubledgerRef is null))
        {
            throw new InvalidOperationException($"Line {rule.Code}: subledger reference must be present exactly when the rule line has a subledger.");
        }
    }

    private static async Task<ActiveRule> ActiveRuleAsync(CommandContext context, string ruleCode, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT r.posting_rule_id, v.version, r.event_type, v.close_component, v.definition::text
            FROM fin.posting_rule r
            JOIN fin.posting_rule_version v ON v.posting_rule_id = r.posting_rule_id
            WHERE r.code = @code AND v.status = 'ACTIVE' AND v.effective_from <= @date AND (v.effective_to IS NULL OR v.effective_to > @date)
            """,
            ("code", ruleCode),
            ("date", date));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"No ACTIVE version of posting rule {ruleCode} for {date:yyyy-MM-dd}.");
        }

        return new ActiveRule(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), RuleDefinition.Parse(reader.GetString(4)));
    }

    /// <summary>
    /// E-PR05-4: the period of the business date if the rule's close component is open there; otherwise the first day of the
    /// next period where it is open (late entry). Each candidate period is locked in shared mode so a concurrent close
    /// (exclusive lock, PR-16) cannot interleave (Patch 1 K-20).
    /// </summary>
    private static async Task<(Guid PeriodId, DateOnly PostingDate, bool LateEntry)> ResolvePostingDateAsync(
        CommandContext context,
        string component,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(Guid PeriodId, DateOnly StartsOn)>();
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT period_id, starts_on FROM fin.period
            WHERE company_id = @company AND ends_on >= @date
            ORDER BY starts_on
            """,
            ("company", context.CompanyId),
            ("date", businessDate)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1)));
            }
        }

        if (candidates.Count == 0 || candidates[0].StartsOn > businessDate)
        {
            throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"No accounting period contains {businessDate:yyyy-MM-dd}.");
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var (periodId, startsOn) = candidates[i];
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "SELECT pg_advisory_xact_lock_shared(hashtextextended('period:' || @company || ':' || @period || ':' || @component, 0))",
                cancellationToken,
                ("company", context.CompanyId.ToString()),
                ("period", periodId.ToString()),
                ("component", component)).ConfigureAwait(false);

            var status = await ScalarAsync<string>(
                context,
                "SELECT status FROM fin.close_component_state WHERE period_id = @p AND component = @c",
                cancellationToken,
                ("p", periodId),
                ("c", component));
            if (status is "OPEN" or "REOPENED")
            {
                return i == 0 ? (periodId, businessDate, false) : (periodId, startsOn, true);
            }
        }

        throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"Component {component} is closed from {businessDate:yyyy-MM-dd} on; no open period to post to.");
    }

    private static async Task<(Guid AccountId, Guid MapId)> ResolveAccountAsync(CommandContext context, string role, string? category, DateOnly postingDate, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT account_id, map_id FROM fin.account_role_map
            WHERE company_id = @company AND account_role = @role AND status = 'ACTIVE'
              AND (item_category IS NULL OR item_category = @category)
              AND effective_from <= @date AND (effective_to IS NULL OR effective_to > @date)
            ORDER BY item_category NULLS LAST
            LIMIT 1
            """,
            ("company", context.CompanyId),
            ("role", role),
            ("category", category),
            ("date", postingDate));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"No ACTIVE account mapping for role {role}{(category is null ? string.Empty : $" / {category}")} on {postingDate:yyyy-MM-dd}.");
        }

        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    private static Task<string?> ItemCategoryAsync(CommandContext context, Guid itemId, CancellationToken cancellationToken)
        => ScalarAsync<string>(context, "SELECT item_category FROM md.item WHERE company_id = @c AND item_id = @i", cancellationToken, ("c", context.CompanyId), ("i", itemId));

    private static async Task EnsureEventTypeAsync(CommandContext context, Guid eventId, string expected, CancellationToken cancellationToken)
    {
        var actual = await ScalarAsync<string>(context, "SELECT event_type FROM core.domain_event WHERE event_id = @e", cancellationToken, ("e", eventId));
        if (actual != expected)
        {
            throw new InvalidOperationException($"Posting rule expects event {expected}, but source event is {actual ?? "missing"}.");
        }
    }

    private static Task InsertJournalAsync(CommandContext context, GlJournalRow j, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.gl_journal (journal_id, company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version,
              posting_generation, journal_type, reverses_journal_id, late_entry, occurred_at, row_hash)
            VALUES (@journal_id, @company_id, @posting_date, @period_id, @source_event_id, @rule_id, @rule_version,
              @generation, @journal_type, @reverses, @late_entry, @occurred_at, @row_hash)
            """,
            cancellationToken,
            ("journal_id", j.JournalId),
            ("company_id", j.CompanyId),
            ("posting_date", j.PostingDate),
            ("period_id", j.PeriodId),
            ("source_event_id", j.SourceEventId),
            ("rule_id", j.PostingRuleId),
            ("rule_version", j.PostingRuleVersion),
            ("generation", j.PostingGeneration),
            ("journal_type", j.JournalType),
            ("reverses", j.ReversesJournalId),
            ("late_entry", j.LateEntry),
            ("occurred_at", j.OccurredAt),
            ("row_hash", j.ComputeRowHash()));

    private static async Task WriteEntriesAsync(CommandContext context, Guid periodId, IReadOnlyList<GlEntryRow> entries, CancellationToken cancellationToken)
    {
        foreach (var e in entries)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit,
                  currency, plant_id, item_id, party_id, subledger_type, subledger_ref, inv_value_entry_id, source_event_id, rule_line_code,
                  determination_inputs, row_hash)
                VALUES (@id, @journal_id, @line_no, @company_id, @posting_date, @account_id, @account_role, @debit, @credit,
                  @currency, @plant_id, @item_id, @party_id, @subledger_type, @subledger_ref, @inv_value_entry_id, @source_event_id, @rule_line_code,
                  CAST(@inputs AS jsonb), @row_hash)
                """,
                cancellationToken,
                ("id", e.GlEntryId),
                ("journal_id", e.JournalId),
                ("line_no", e.LineNo),
                ("company_id", e.CompanyId),
                ("posting_date", e.PostingDate),
                ("account_id", e.AccountId),
                ("account_role", e.AccountRole),
                ("debit", e.Debit),
                ("credit", e.Credit),
                ("currency", e.Currency),
                ("plant_id", e.PlantId),
                ("item_id", e.ItemId),
                ("party_id", e.PartyId),
                ("subledger_type", e.SubledgerType),
                ("subledger_ref", e.SubledgerRef),
                ("inv_value_entry_id", e.InvValueEntryId),
                ("source_event_id", e.SourceEventId),
                ("rule_line_code", e.RuleLineCode),
                ("inputs", e.DeterminationInputs),
                ("row_hash", e.ComputeRowHash())).ConfigureAwait(false);
        }

        // Balance projection last, in a stable key order (lock order level 9).
        foreach (var group in entries
            .GroupBy(e => (e.AccountId, e.PlantId, e.PartyId))
            .OrderBy(g => g.Key.AccountId).ThenBy(g => g.Key.PlantId).ThenBy(g => g.Key.PartyId))
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.gl_period_balance (company_id, period_id, account_id, plant_id, party_id, debit, credit)
                VALUES (@company_id, @period_id, @account_id, @plant_id, @party_id, @debit, @credit)
                ON CONFLICT (company_id, period_id, account_id, plant_id, party_id)
                DO UPDATE SET debit = fin.gl_period_balance.debit + EXCLUDED.debit, credit = fin.gl_period_balance.credit + EXCLUDED.credit
                """,
                cancellationToken,
                ("company_id", context.CompanyId),
                ("period_id", periodId),
                ("account_id", group.Key.AccountId),
                ("plant_id", group.Key.PlantId),
                ("party_id", group.Key.PartyId),
                ("debit", group.Sum(e => e.Debit)),
                ("credit", group.Sum(e => e.Credit))).ConfigureAwait(false);
        }
    }

    private static async Task<JournalHeader?> ReadJournalHeaderAsync(CommandContext context, Guid journalId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT posting_rule_id, posting_rule_version, journal_type FROM fin.gl_journal WHERE company_id = @c AND journal_id = @j",
            ("c", context.CompanyId),
            ("j", journalId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new JournalHeader(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2))
            : null;
    }

    private static async Task<bool> IsReversedAsync(CommandContext context, Guid journalId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM fin.gl_journal WHERE reverses_journal_id = @j)",
            ("j", journalId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<List<GlEntryRow>> ReadEntriesAsync(CommandContext context, Guid journalId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, currency,
                   plant_id, item_id, party_id, subledger_type, subledger_ref, inv_value_entry_id, source_event_id, rule_line_code,
                   determination_inputs::text
            FROM fin.gl_entry WHERE journal_id = @j ORDER BY line_no
            """,
            ("j", journalId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<GlEntryRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadEntry(reader));
        }

        return rows;
    }

    /// <summary>Reads a fin.gl_entry row in declared column order (used by tests to recompute row hashes).</summary>
    public static GlEntryRow ReadEntry(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new GlEntryRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetGuid(3), reader.GetFieldValue<DateOnly>(4), reader.GetGuid(5),
            reader.GetString(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetString(9).Trim(),
            reader.IsDBNull(10) ? null : reader.GetGuid(10), reader.IsDBNull(11) ? null : reader.GetGuid(11), reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetGuid(14), reader.IsDBNull(15) ? null : reader.GetGuid(15),
            reader.GetGuid(16), reader.GetString(17), reader.GetString(18));
    }

    private static async Task<T?> ScalarAsync<T>(CommandContext context, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    private sealed record ActiveRule(Guid RuleId, int Version, string EventType, string CloseComponent, RuleDefinition Definition);

    private sealed record JournalHeader(Guid RuleId, int RuleVersion, string JournalType);
}
