using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;

namespace Rochell.Tax;

/// <summary>
/// One line to determine: its id in the subject document, the item and its net amount (quantity × price). An expense line
/// (E-GAS-02-4) has no item: it names its tax type (a PURCHASE_TAX_TYPE rule) and whether its category is a service or a good
/// (<see cref="TaxLineScopes"/>).
/// </summary>
public sealed record TaxLineInput(Guid SubjectLineId, Guid? ItemId, decimal NetAmount, Guid? TaxTypeRuleId = null, string? ExpenseScope = null);

/// <summary>
/// What to determine taxes for: the subject document, its supplier or customer and the determination date. A SALE applies only
/// SALES_ITBIS rules and a PURCHASE only the purchase rules (E-VS3-05-1).
/// </summary>
public sealed record TaxRequest(
    string SubjectType, Guid SubjectId, DateOnly Date, Guid PartyId, IReadOnlyList<TaxLineInput> Lines, string Direction = TaxDirections.Purchase, TaxExemption? Exemption = null);

/// <summary>
/// E-UX4-3: the estimated ITBIS of a draft, per line id and in total (2 decimals); both null when the fiscal gate is closed, with
/// <see cref="UnavailableCode"/> (FISCAL_GATE_CLOSED) and <see cref="UnavailableReason"/> saying why.
/// </summary>
public sealed record ItbisEstimate(IReadOnlyDictionary<Guid, decimal>? ByLine, decimal? Total, string? UnavailableCode, string? UnavailableReason);

/// <summary>E-FIS1-03-3: a sale covered by an ACTIVE fiscal authorization carries no ITBIS; the determination records why.</summary>
public sealed record TaxExemption(Guid AuthorizationId, string Regime, string CertificateNo);

public static class TaxDirections
{
    public const string Purchase = "PURCHASE";
    public const string Sale = "SALE";
}

/// <summary>The recorded determination.</summary>
public sealed record TaxDetermination(Guid DeterminationId, IReadOnlyList<DeterminedTax> Taxes)
{
    public bool HasNonRecoverableInput => Taxes.Any(t => t.Effect == TaxEffects.NonRecoverableInput);

    public decimal RecoverableInput => Taxes.Where(t => t.Effect == TaxEffects.RecoverableInput).Sum(t => t.Amount);

    public decimal Withholding => Taxes.Where(t => t.Effect == TaxEffects.Withholding).Sum(t => t.Amount);
}

/// <summary>
/// §30 purchase Tax Engine behind the fiscal gate. Closed gate (FISCAL_GATE_CLOSED): no ACTIVE purchase ITBIS rule for the
/// date, or any rule with a version pending activation already in force on the date and no ACTIVE version covering it (SI-07).
/// The determination (inputs, versions used, taxes) is recorded append-only in the caller's transaction.
/// </summary>
public sealed class TaxEngine
{
    public async Task<TaxDetermination> DetermineAsync(CommandContext context, TaxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count == 0 || string.IsNullOrWhiteSpace(request.SubjectType))
        {
            throw new DomainException(TaxErrors.SubjectInvalid, "A determination needs a subject and at least one line.");
        }

        var sale = request.Direction == TaxDirections.Sale;
        if (request.Lines.Any(l => (l.ItemId is null) == (l.TaxTypeRuleId is null) || (l.TaxTypeRuleId is not null && (sale || l.ExpenseScope is not (TaxLineScopes.ExpenseService or TaxLineScopes.ExpenseGoods)))))
        {
            throw new DomainException(TaxErrors.SubjectInvalid, "A line has an item, or — on a purchase — a tax type with the scope of its expense category; never both.");
        }

        var rules = await ApplicableRulesAsync(
            context.Connection, context.Transaction, context.CompanyId, request.Date, sale, request.Lines.Select(l => l.TaxTypeRuleId).OfType<Guid>().ToHashSet(),
            request.Lines.Any(l => l.ItemId is not null), cancellationToken).ConfigureAwait(false);
        var partyType = await PartyTypeAsync(context, request.PartyId, cancellationToken).ConfigureAwait(false);
        var lines = new List<TaxableLine>();
        foreach (var line in request.Lines)
        {
            lines.Add(line.ItemId is { } item
                ? new TaxableLine(line.SubjectLineId, await ItemCategoryAsync(context, item, cancellationToken).ConfigureAwait(false), line.NetAmount)
                : new TaxableLine(line.SubjectLineId, string.Empty, line.NetAmount, line.TaxTypeRuleId, line.ExpenseScope!));
        }

        // E-FIS1-03-3: an exempt sale keeps the gate (the rules in force are still required and recorded) and determines no ITBIS.
        var taxes = request.Exemption is null ? TaxCalculator.Determine(partyType, lines, rules) : [];
        var determinationId = context.Ids.NewId();
        var recorded = new Dictionary<string, object>
        {
            ["date"] = request.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["partyId"] = request.PartyId,
            ["partyTaxType"] = partyType,
            ["lines"] = lines.Select(l => l.TaxTypeRuleId is { } taxType
                ? (object)new { lineId = l.LineId, taxType = rules.First(r => r.RuleId == taxType).RuleCode, scope = l.Scope, netAmount = l.NetAmount.ToString(CultureInfo.InvariantCulture) }
                : new { lineId = l.LineId, itemCategory = l.ItemCategory, netAmount = l.NetAmount.ToString(CultureInfo.InvariantCulture) }).ToList(),
        };
        if (request.Exemption is { } exemption)
        {
            recorded["exemption"] = new { authorizationId = exemption.AuthorizationId, regime = exemption.Regime, certificateNo = exemption.CertificateNo };
        }

        var inputs = JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(recorded));
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.tax_determination (determination_id, company_id, subject_type, subject_id, determination_date, rule_version_ids, inputs, determined_at)
            VALUES (@id, @c, @type, @subject, @date, @versions, CAST(@inputs AS jsonb), @at)
            """,
            cancellationToken,
            ("id", determinationId),
            ("c", context.CompanyId),
            ("type", request.SubjectType),
            ("subject", request.SubjectId),
            ("date", request.Date),
            ("versions", rules.Select(r => r.RuleVersionId).ToArray()),
            ("inputs", inputs),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);

        var lineNo = 0;
        foreach (var tax in taxes)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO tax.tax_determination_line (determination_id, line_no, company_id, subject_line_id, rule_version_id, tax_code, base, rate, amount, effect)
                VALUES (@id, @no, @c, @line, @version, @code, @base, @rate, @amount, @effect)
                """,
                cancellationToken,
                ("id", determinationId),
                ("no", ++lineNo),
                ("c", context.CompanyId),
                ("line", tax.LineId),
                ("version", tax.RuleVersionId),
                ("code", tax.TaxCode),
                ("base", tax.Base),
                ("rate", tax.Rate),
                ("amount", tax.Amount),
                ("effect", tax.Effect)).ConfigureAwait(false);
        }

        return new TaxDetermination(determinationId, taxes);
    }

    /// <summary>
    /// E-FIS1-02-8: the sales ITBIS a set of lines would carry on <paramref name="date"/>, computed with the rules in force and never
    /// written (the proforma a customer takes to the DGII). Closed gate → FISCAL_GATE_CLOSED, as at invoicing.
    /// </summary>
    public static async Task<IReadOnlyList<DeterminedTax>> PreviewSalesItbisAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, DateOnly date, IReadOnlyList<TaxableLine> lines, CancellationToken cancellationToken)
    {
        var rules = await ApplicableRulesAsync(connection, transaction, companyId, date, sale: true, NoTaxTypes, needsItbis: true, cancellationToken).ConfigureAwait(false);
        return TaxCalculator.Determine(PartyTaxTypes.Company, lines, rules);
    }

    /// <summary>
    /// E-UX4-3: the ITBIS a draft purchase or sale would carry on <paramref name="date"/> — the rules in force applied by the same
    /// calculator, per line and in total — without writing anything. A closed fiscal gate is not an error here: the estimate is null
    /// and says why (the code and message the posting or invoicing would give).
    /// </summary>
    public static async Task<ItbisEstimate> EstimateItbisAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, DateOnly date, bool sale, IReadOnlyList<TaxLineInput> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        IReadOnlyList<ApplicableRule> rules;
        try
        {
            rules = await ApplicableRulesAsync(
                connection, transaction, companyId, date, sale, lines.Select(l => l.TaxTypeRuleId).OfType<Guid>().ToHashSet(), lines.Any(l => l.ItemId is not null), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Code == TaxErrors.FiscalGateClosed)
        {
            return new ItbisEstimate(null, null, ex.Code, ex.Message);
        }

        var categories = new Dictionary<Guid, string>();
        await using (var command = Sql.Command(
            connection, transaction, "SELECT item_id, item_category FROM md.item WHERE company_id = @c AND item_id = ANY(@ids)", ("c", companyId), ("ids", lines.Select(l => l.ItemId).OfType<Guid>().Distinct().ToArray())))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                categories[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        // E-GAS-02-4: an expense line of a draft is estimated with its own tax type — every component, not only the ITBIS.
        var taxable = lines.Select(l => l.ItemId is { } item
            ? new TaxableLine(
                l.SubjectLineId,
                categories.TryGetValue(item, out var category) ? category : throw new DomainException(TaxErrors.SubjectInvalid, "An item of the estimate does not exist."),
                l.NetAmount)
            : new TaxableLine(l.SubjectLineId, string.Empty, l.NetAmount, l.TaxTypeRuleId, l.ExpenseScope ?? TaxLineScopes.ExpenseService)).ToList();
        var itbis = TaxCalculator.Determine(PartyTaxTypes.Company, taxable, rules).Where(t => t.Effect != TaxEffects.Withholding).ToList();
        var zero = new decimal(0, 0, 0, false, 2);
        var byLine = lines.ToDictionary(l => l.SubjectLineId, l => zero + itbis.Where(t => t.LineId == l.SubjectLineId).Sum(t => t.Amount));
        return new ItbisEstimate(byLine, zero + byLine.Values.Sum(), null, null);
    }

    private static readonly IReadOnlySet<Guid> NoTaxTypes = new HashSet<Guid>();

    /// <summary>
    /// The rules in force for a determination. <paramref name="taxTypes"/> (E-GAS-02-4): the tax types its expense lines name — each
    /// must be ACTIVE on the date, and only those apply or close the gate (a type nobody uses, or one still pending, stops nothing).
    /// <paramref name="needsItbis"/>: there are lines with an item, so the ITBIS rule of the direction must be in force.
    /// </summary>
    private static async Task<IReadOnlyList<ApplicableRule>> ApplicableRulesAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, DateOnly date, bool sale, IReadOnlySet<Guid> taxTypes, bool needsItbis,
        CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT r.code, r.rule_kind, r.rule_id,
                   (SELECT v.rule_version_id FROM tax.fiscal_rule_version v
                    WHERE v.rule_id = r.rule_id AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)) AS active_version,
                   (SELECT v.definition::text FROM tax.fiscal_rule_version v
                    WHERE v.rule_id = r.rule_id AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)) AS definition,
                   EXISTS (SELECT 1 FROM tax.fiscal_rule_version v
                           WHERE v.rule_id = r.rule_id AND v.status IN ('BLOCKED_PENDING_SOURCE', 'READY') AND v.effective_from <= @d) AS pending
            FROM tax.fiscal_rule r
            WHERE r.company_id = @c
            ORDER BY r.code
            """,
            ("c", companyId),
            ("d", date));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<ApplicableRule>();
        var closed = new List<string>();
        var found = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var code = reader.GetString(0);
            var kind = reader.GetString(1);
            var ruleId = reader.GetGuid(2);
            if (FiscalRuleKinds.IsReport(kind))
            {
                continue; // E-FIS2-01-3: read by the reports only; it neither applies nor closes the gate
            }

            if (kind == FiscalRuleKinds.PurchaseTaxType)
            {
                if (sale || !taxTypes.Contains(ruleId))
                {
                    continue; // E-GAS-02-4: only the tax types the lines name
                }

                found.Add(ruleId);
                if (reader.IsDBNull(3))
                {
                    closed.Add(code);
                    continue;
                }
            }
            else if (FiscalRuleKinds.IsSales(kind) != sale)
            {
                continue; // the other direction's rules neither apply nor close this gate
            }

            if (reader.IsDBNull(3))
            {
                if (reader.GetBoolean(5))
                {
                    closed.Add(code);
                }

                continue;
            }

            rules.Add(new ApplicableRule(reader.GetGuid(3), code, FiscalRuleDefinition.Parse(kind, reader.GetString(4)), ruleId));
        }

        if (taxTypes.Count != found.Count)
        {
            throw new DomainException(TaxErrors.SubjectInvalid, "A line names a tax type that does not exist.");
        }

        if (closed.Count > 0)
        {
            throw new DomainException(TaxErrors.FiscalGateClosed, $"Fiscal rules not in force on {date:yyyy-MM-dd}, or pending activation: {string.Join(", ", closed)}.");
        }

        var itbisKind = sale ? FiscalRuleKinds.SalesItbis : FiscalRuleKinds.PurchaseItbis;
        if (needsItbis && !rules.Any(r => r.Definition.Kind == itbisKind))
        {
            throw new DomainException(TaxErrors.FiscalGateClosed, $"No {(sale ? "sales" : "purchase")} ITBIS rule is active on {date:yyyy-MM-dd}.");
        }

        return rules;
    }

    /// <summary>
    /// E-CF1-3, E-CF1-01-6: the total from which a sale to the final consumer must identify its buyer, from the CONSUMER_ID_THRESHOLD
    /// rule in force on <paramref name="date"/>. Null when no such rule is active: the caller refuses the sale.
    /// </summary>
    public static async Task<decimal?> ConsumerIdThresholdAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT v.definition::text FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id
            WHERE r.company_id = @c AND r.rule_kind = @kind AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            ORDER BY v.effective_from DESC LIMIT 1
            """,
            ("c", companyId),
            ("kind", FiscalRuleKinds.ConsumerIdThreshold),
            ("d", date));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string definition
            ? FiscalRuleDefinition.Parse(FiscalRuleKinds.ConsumerIdThreshold, definition).Amount
            : null;
    }

    private static async Task<string> PartyTypeAsync(CommandContext context, Guid partyId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT coalesce(rnc, '') FROM md.party WHERE company_id = @c AND party_id = @p", ("c", context.CompanyId), ("p", partyId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string rnc
            ? PartyTaxTypes.FromIdentifier(rnc.Length == 0 ? null : rnc)
            : throw new DomainException(TaxErrors.SubjectInvalid, "The supplier does not exist.");
    }

    private static async Task<string> ItemCategoryAsync(CommandContext context, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT item_category FROM md.item WHERE company_id = @c AND item_id = @i", ("c", context.CompanyId), ("i", itemId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw new DomainException(TaxErrors.SubjectInvalid, "An item of the determination does not exist.");
    }
}
