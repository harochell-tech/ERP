namespace Rochell.Tax;

/// <summary>
/// One line to tax: its net amount and the item's category. An expense line (E-GAS-02-4) has no item: it names its tax type
/// (<paramref name="TaxTypeRuleId"/>) and its scope says whether its category is a service or a good.
/// </summary>
public sealed record TaxableLine(Guid LineId, string ItemCategory, decimal NetAmount, Guid? TaxTypeRuleId = null, string Scope = TaxLineScopes.Inventory);

/// <summary>An applicable rule version. <paramref name="RuleId"/> is what an expense line's tax type names.</summary>
public sealed record ApplicableRule(Guid RuleVersionId, string RuleCode, FiscalRuleDefinition Definition, Guid? RuleId = null);

/// <summary>One determined tax (E-PR12-7: base and amount with 2 decimals, half-up).</summary>
public sealed record DeterminedTax(Guid LineId, Guid RuleVersionId, string TaxCode, decimal Base, decimal Rate, decimal Amount, string Effect);

/// <summary>§30: a pure function from context to determination. No I/O, no clock, no normative literal.</summary>
public static class TaxCalculator
{
    public static IReadOnlyList<DeterminedTax> Determine(string partyTaxType, IReadOnlyList<TaxableLine> lines, IReadOnlyList<ApplicableRule> rules)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(rules);
        var result = new List<DeterminedTax>();
        foreach (var line in lines)
        {
            // E-GAS-02-4: an expense line carries only the components of its own tax type; the ITBIS rule by item category is for
            // lines with an item.
            var itbis = line.TaxTypeRuleId is { } taxType
                ? rules.Where(r => r.Definition.Kind == FiscalRuleKinds.PurchaseTaxType && r.RuleId == taxType).SelectMany(r => Components(r, line)).ToList()
                : rules.Where(r => r.Definition.Kind is FiscalRuleKinds.PurchaseItbis or FiscalRuleKinds.SalesItbis)
                    .Select(r => Itbis(r, line))
                    .OfType<DeterminedTax>()
                    .ToList();
            result.AddRange(itbis);

            // E-GAS-02-5: a withholding on ITBIS is on the line's ITBIS, not on its selective tax, other taxes or tip.
            var itbisAmount = itbis.Where(t => t.Effect is TaxEffects.RecoverableInput or TaxEffects.NonRecoverableInput or TaxEffects.Output).Sum(t => t.Amount);
            result.AddRange(rules.Where(r => r.Definition.Kind == FiscalRuleKinds.PurchaseWithholding && (r.Definition.AppliesTo?.Contains(line.Scope) ?? true))
                .Select(r => Withholding(r, line, partyTaxType, itbisAmount))
                .OfType<DeterminedTax>());
        }

        return result;
    }

    public static DeterminedTax? Itbis(ApplicableRule rule, TaxableLine line)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(line);
        var net = Money(line.NetAmount);
        return rule.Definition.ExemptItemCategories.Contains(line.ItemCategory) || net <= 0
            ? null
            : Tax(rule, line.LineId, net, rule.Definition.Effect);
    }

    /// <summary>E-GAS-02-1, E-GAS-01-6: every component of the line's tax type, on the line's net; none for an exempt type.</summary>
    public static IEnumerable<DeterminedTax> Components(ApplicableRule rule, TaxableLine line)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(line);
        var net = Money(line.NetAmount);
        return net <= 0
            ? []
            : (rule.Definition.Components ?? []).Select(c => new DeterminedTax(line.LineId, rule.RuleVersionId, c.TaxCode, net, c.Rate, Money(net * c.Rate), c.Effect));
    }

    public static DeterminedTax? Withholding(ApplicableRule rule, TaxableLine line, string partyTaxType, decimal itbisAmount)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(line);
        if (!rule.Definition.PartyTypes.Contains(partyTaxType) || !(rule.Definition.AppliesTo?.Contains(line.Scope) ?? true))
        {
            return null;
        }

        var @base = Money(rule.Definition.Base == "ITBIS" ? itbisAmount : line.NetAmount);
        return @base <= 0 ? null : Tax(rule, line.LineId, @base, TaxEffects.Withholding);
    }

    private static DeterminedTax Tax(ApplicableRule rule, Guid lineId, decimal @base, string effect)
        => new(lineId, rule.RuleVersionId, rule.Definition.TaxCode, @base, rule.Definition.Rate, Money(@base * rule.Definition.Rate), effect);

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
