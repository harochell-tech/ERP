using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;

namespace Rochell.Tax;

/// <summary>
/// E-PR12-3: declarative definition of a purchase fiscal rule. Every normative value (code, rate, exemptions, who is
/// withheld) comes from the activated definition, never from code.
/// PURCHASE_ITBIS: {"tax_code","rate","effect" (RECOVERABLE_INPUT | NON_RECOVERABLE_INPUT), "exempt_item_categories"?}.
/// PURCHASE_WITHHOLDING: {"tax_code","rate","base" (NET | ITBIS),"party_types" (COMPANY | INDIVIDUAL)}.
/// Rates are decimal strings (E-PR06-5), 0 &lt; rate ≤ 1, at most 6 decimals.
/// </summary>
public sealed record FiscalRuleDefinition(
    string Kind,
    string TaxCode,
    decimal Rate,
    string Effect,
    IReadOnlySet<string> ExemptItemCategories,
    string? Base,
    IReadOnlySet<string> PartyTypes)
{
    /// <summary>The closed item category list of md.item (E-PR04-7).</summary>
    public static readonly IReadOnlySet<string> ItemCategories = new HashSet<string>(StringComparer.Ordinal) { "CEMENTO", "AGREGADO", "ADITIVO", "OTRA_MATERIA_PRIMA" };

    private static readonly string[] ItbisKeys = ["tax_code", "rate", "effect", "exempt_item_categories"];
    private static readonly string[] WithholdingKeys = ["tax_code", "rate", "base", "party_types"];

    public static FiscalRuleDefinition Parse(string kind, string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw Invalid($"The definition is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The definition must be a JSON object.");
        }

        var allowed = kind switch
        {
            FiscalRuleKinds.PurchaseItbis => ItbisKeys,
            FiscalRuleKinds.PurchaseWithholding => WithholdingKeys,
            _ => throw Invalid($"Unknown rule kind {kind}."),
        };
        foreach (var property in root.EnumerateObject().Where(p => !allowed.Contains(p.Name)))
        {
            throw Invalid($"Unknown key '{property.Name}' for {kind}.");
        }

        var taxCode = RequiredString(root, "tax_code");
        if (!System.Text.RegularExpressions.Regex.IsMatch(taxCode, "^[A-Z][A-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            throw Invalid("tax_code must be uppercase letters, digits and underscores.");
        }

        var rateText = RequiredString(root, "rate");
        if (!decimal.TryParse(rateText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) || rate <= 0 || rate > 1 || decimal.Round(rate, 6) != rate)
        {
            throw Invalid("rate must be a decimal string greater than 0 and at most 1, with at most 6 decimals.");
        }

        if (kind == FiscalRuleKinds.PurchaseItbis)
        {
            var effect = RequiredString(root, "effect");
            if (effect is not (TaxEffects.RecoverableInput or TaxEffects.NonRecoverableInput))
            {
                throw Invalid("effect must be RECOVERABLE_INPUT or NON_RECOVERABLE_INPUT.");
            }

            var exempt = root.TryGetProperty("exempt_item_categories", out var categories) ? StringSet(categories, "exempt_item_categories", ItemCategories) : new HashSet<string>(StringComparer.Ordinal);
            return new FiscalRuleDefinition(kind, taxCode, rate, effect, exempt, null, new HashSet<string>(StringComparer.Ordinal));
        }

        var @base = RequiredString(root, "base");
        if (@base is not ("NET" or "ITBIS"))
        {
            throw Invalid("base must be NET or ITBIS.");
        }

        if (!root.TryGetProperty("party_types", out var partyTypes))
        {
            throw Invalid("party_types is required.");
        }

        var parties = StringSet(partyTypes, "party_types", new HashSet<string>(StringComparer.Ordinal) { PartyTaxTypes.Company, PartyTaxTypes.Individual });
        if (parties.Count == 0)
        {
            throw Invalid("party_types cannot be empty.");
        }

        return new FiscalRuleDefinition(kind, taxCode, rate, TaxEffects.Withholding, new HashSet<string>(StringComparer.Ordinal), @base, parties);
    }

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw Invalid($"{name} is required and must be a non-empty string.");

    private static HashSet<string> StringSet(JsonElement value, string name, IReadOnlySet<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{name} must be an array of strings.");
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in value.EnumerateArray())
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString()! : throw Invalid($"{name} must contain only strings.");
            if (!allowed.Contains(text))
            {
                throw Invalid($"{name}: '{text}' is not one of {string.Join(", ", allowed)}.");
            }

            if (!set.Add(text))
            {
                throw Invalid($"{name}: '{text}' is repeated.");
            }
        }

        return set;
    }

    private static DomainException Invalid(string message) => new(TaxErrors.FiscalRuleInvalid, message);
}
