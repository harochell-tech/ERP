using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;

namespace Rochell.Tax;

/// <summary>One purchase line to determine: its id in the subject document, the item and its net amount (quantity × price).</summary>
public sealed record TaxLineInput(Guid SubjectLineId, Guid ItemId, decimal NetAmount);

/// <summary>
/// What to determine taxes for: the subject document, its supplier or customer and the determination date. A SALE applies only
/// SALES_ITBIS rules and a PURCHASE only the purchase rules (E-VS3-05-1).
/// </summary>
public sealed record TaxRequest(
    string SubjectType, Guid SubjectId, DateOnly Date, Guid PartyId, IReadOnlyList<TaxLineInput> Lines, string Direction = TaxDirections.Purchase, TaxExemption? Exemption = null);

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

        var rules = await ApplicableRulesAsync(context, request.Date, request.Direction == TaxDirections.Sale, cancellationToken).ConfigureAwait(false);
        var partyType = await PartyTypeAsync(context, request.PartyId, cancellationToken).ConfigureAwait(false);
        var lines = new List<TaxableLine>();
        foreach (var line in request.Lines)
        {
            lines.Add(new TaxableLine(line.SubjectLineId, await ItemCategoryAsync(context, line.ItemId, cancellationToken).ConfigureAwait(false), line.NetAmount));
        }

        // E-FIS1-03-3: an exempt sale keeps the gate (the rules in force are still required and recorded) and determines no ITBIS.
        var taxes = request.Exemption is null ? TaxCalculator.Determine(partyType, lines, rules) : [];
        var determinationId = context.Ids.NewId();
        var recorded = new Dictionary<string, object>
        {
            ["date"] = request.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["partyId"] = request.PartyId,
            ["partyTaxType"] = partyType,
            ["lines"] = lines.Select(l => new { lineId = l.LineId, itemCategory = l.ItemCategory, netAmount = l.NetAmount.ToString(CultureInfo.InvariantCulture) }).ToList(),
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

    private static Task<IReadOnlyList<ApplicableRule>> ApplicableRulesAsync(CommandContext context, DateOnly date, bool sale, CancellationToken cancellationToken)
        => ApplicableRulesAsync(context.Connection, context.Transaction, context.CompanyId, date, sale, cancellationToken);

    /// <summary>
    /// E-FIS1-02-8: the sales ITBIS a set of lines would carry on <paramref name="date"/>, computed with the rules in force and never
    /// written (the proforma a customer takes to the DGII). Closed gate → FISCAL_GATE_CLOSED, as at invoicing.
    /// </summary>
    public static async Task<IReadOnlyList<DeterminedTax>> PreviewSalesItbisAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, DateOnly date, IReadOnlyList<TaxableLine> lines, CancellationToken cancellationToken)
    {
        var rules = await ApplicableRulesAsync(connection, transaction, companyId, date, sale: true, cancellationToken).ConfigureAwait(false);
        return TaxCalculator.Determine(PartyTaxTypes.Company, lines, rules);
    }

    private static async Task<IReadOnlyList<ApplicableRule>> ApplicableRulesAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, DateOnly date, bool sale, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT r.code, r.rule_kind,
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
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var code = reader.GetString(0);
            if (FiscalRuleKinds.IsSales(reader.GetString(1)) != sale)
            {
                continue; // the other direction's rules neither apply nor close this gate
            }

            if (reader.IsDBNull(2))
            {
                if (reader.GetBoolean(4))
                {
                    closed.Add(code);
                }

                continue;
            }

            rules.Add(new ApplicableRule(reader.GetGuid(2), code, FiscalRuleDefinition.Parse(reader.GetString(1), reader.GetString(3))));
        }

        if (closed.Count > 0)
        {
            throw new DomainException(TaxErrors.FiscalGateClosed, $"Fiscal rules pending activation on {date:yyyy-MM-dd}: {string.Join(", ", closed)}.");
        }

        var itbisKind = sale ? FiscalRuleKinds.SalesItbis : FiscalRuleKinds.PurchaseItbis;
        if (!rules.Any(r => r.Definition.Kind == itbisKind))
        {
            throw new DomainException(TaxErrors.FiscalGateClosed, $"No {(sale ? "sales" : "purchase")} ITBIS rule is active on {date:yyyy-MM-dd}.");
        }

        return rules;
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
