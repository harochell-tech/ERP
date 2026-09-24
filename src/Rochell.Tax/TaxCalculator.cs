namespace Rochell.Tax;

/// <summary>One purchase line to tax: its net amount and the item's category.</summary>
public sealed record TaxableLine(Guid LineId, string ItemCategory, decimal NetAmount);

/// <summary>An applicable rule version.</summary>
public sealed record ApplicableRule(Guid RuleVersionId, string RuleCode, FiscalRuleDefinition Definition);

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
            var itbis = rules.Where(r => r.Definition.Kind == FiscalRuleKinds.PurchaseItbis)
                .Select(r => Itbis(r, line))
                .OfType<DeterminedTax>()
                .ToList();
            result.AddRange(itbis);
            var itbisAmount = itbis.Sum(t => t.Amount);
            result.AddRange(rules.Where(r => r.Definition.Kind == FiscalRuleKinds.PurchaseWithholding)
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

    public static DeterminedTax? Withholding(ApplicableRule rule, TaxableLine line, string partyTaxType, decimal itbisAmount)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(line);
        if (!rule.Definition.PartyTypes.Contains(partyTaxType))
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
