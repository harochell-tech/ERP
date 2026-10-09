using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;

namespace Rochell.Tax;

/// <summary>One tax of a purchase tax type (E-GAS-02-1): its code, its rate on the line's net and where it goes.</summary>
public sealed record TaxComponent(string TaxCode, decimal Rate, string Effect);

/// <summary>
/// E-PR12-3: declarative definition of a purchase fiscal rule. Every normative value (code, rate, exemptions, who is
/// withheld) comes from the activated definition, never from code.
/// PURCHASE_ITBIS: {"tax_code","rate","effect" (RECOVERABLE_INPUT | NON_RECOVERABLE_INPUT), "exempt_item_categories"?}.
/// SALES_ITBIS (E-VS3-05-1): {"tax_code","rate","effect" (OUTPUT), "exempt_item_categories"?}.
/// PURCHASE_WITHHOLDING: {"tax_code","rate","base" (NET | ITBIS),"party_types" (COMPANY | INDIVIDUAL),"isr_withholding_type"? ("1"…"9", E-FIS2-01-4)}.
/// REPORT_606_CLASSIFICATION (E-FIS2-01-1/2): {"classes": {"&lt;raw-material category&gt;": "01"…"11"}} covering every raw-material category.
/// CONSUMER_ID_THRESHOLD (E-CF1-01-6): {"amount"} — a decimal string greater than 0, at most 2 decimals.
/// PURCHASE_TAX_TYPE (E-GAS-02-1): {"label","components": [{"tax_code","rate","effect" (RECOVERABLE_INPUT | SELECTIVE_TAX | OTHER_TAX |
/// LEGAL_TIP)}]} — no components is «exento» (E-GAS-01-5); each component is computed on the line's net (E-GAS-01-6).
/// PURCHASE_WITHHOLDING may carry "applies_to" (INVENTORY | EXPENSE_SERVICE | EXPENSE_GOODS, E-GAS-01-7); without it, every line.
/// Rates are decimal strings (E-PR06-5), 0 &lt; rate ≤ 1, at most 6 decimals.
/// </summary>
public sealed record FiscalRuleDefinition(
    string Kind,
    string TaxCode,
    decimal Rate,
    string Effect,
    IReadOnlySet<string> ExemptItemCategories,
    string? Base,
    IReadOnlySet<string> PartyTypes,
    string? IsrWithholdingType = null,
    IReadOnlyDictionary<string, string>? Classes = null,
    decimal? Amount = null,
    string? Label = null,
    IReadOnlyList<TaxComponent>? Components = null,
    IReadOnlySet<string>? AppliesTo = null,
    IReadOnlySet<string>? DocumentSeries = null)
{
    /// <summary>E-X1-02-3: the supplier documents a withholding applies to — B (series B NCF) and E (e-CF).</summary>
    public static readonly IReadOnlySet<string> SupplierDocumentSeries = new HashSet<string>(StringComparer.Ordinal) { "B", "E" };

    /// <summary>E-FIS2-01-2: the purchased categories the 606 classifies (the raw materials of md.item).</summary>
    public static readonly IReadOnlySet<string> RawMaterialCategories = new HashSet<string>(StringComparer.Ordinal) { "CEMENTO", "AGREGADO", "ADITIVO", "OTRA_MATERIA_PRIMA" };

    /// <summary>The closed item category list of md.item (E-PR04-7).</summary>
    public static readonly IReadOnlySet<string> ItemCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "CEMENTO", "AGREGADO", "ADITIVO", "OTRA_MATERIA_PRIMA", "BLOQUE", "ADOQUIN", "OTRO_PT", // finished goods since E-VS3-05-1
        "TRANSPORTE", // the freight service (E-SRV1-8), exempt through SALES_ITBIS (E-SRV1-16)
    };

    private static readonly string[] ItbisKeys = ["tax_code", "rate", "effect", "exempt_item_categories"];
    private static readonly string[] WithholdingKeys = ["tax_code", "rate", "base", "party_types", "isr_withholding_type", "applies_to", "document_series"];
    private static readonly string[] TaxTypeKeys = ["label", "components"];
    private static readonly string[] ComponentKeys = ["tax_code", "rate", "effect"];
    private static readonly IReadOnlySet<string> ComponentEffects = new HashSet<string>(StringComparer.Ordinal)
    {
        TaxEffects.RecoverableInput, TaxEffects.SelectiveTax, TaxEffects.OtherTax, TaxEffects.LegalTip,
    };
    private static readonly string[] ClassificationKeys = ["classes"];
    private static readonly string[] ThresholdKeys = ["amount"];

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
            FiscalRuleKinds.PurchaseItbis or FiscalRuleKinds.SalesItbis => ItbisKeys,
            FiscalRuleKinds.PurchaseWithholding => WithholdingKeys,
            FiscalRuleKinds.Report606Classification => ClassificationKeys,
            FiscalRuleKinds.ConsumerIdThreshold => ThresholdKeys,
            FiscalRuleKinds.PurchaseTaxType => TaxTypeKeys,
            _ => throw Invalid($"Unknown rule kind {kind}."),
        };
        foreach (var property in root.EnumerateObject().Where(p => !allowed.Contains(p.Name)))
        {
            throw Invalid($"Unknown key '{property.Name}' for {kind}.");
        }

        if (kind == FiscalRuleKinds.Report606Classification)
        {
            return new FiscalRuleDefinition(kind, kind, 0m, kind, new HashSet<string>(StringComparer.Ordinal), null, new HashSet<string>(StringComparer.Ordinal), null, ParseClasses(root));
        }

        if (kind == FiscalRuleKinds.ConsumerIdThreshold)
        {
            // E-CF1-01-6: {"amount": "250000.00"} — the invoice total, in pesos, from which the buyer's identification is mandatory.
            return decimal.TryParse(RequiredString(root, "amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount > 0 && decimal.Round(amount, 2) == amount
                ? new FiscalRuleDefinition(kind, kind, 0m, kind, new HashSet<string>(StringComparer.Ordinal), null, new HashSet<string>(StringComparer.Ordinal), null, null, amount)
                : throw Invalid("amount must be a decimal string greater than 0 with at most 2 decimals.");
        }

        if (kind == FiscalRuleKinds.PurchaseTaxType)
        {
            return ParseTaxType(kind, root);
        }

        var taxCode = TaxCodeOf(root);
        var rate = RateOf(root);

        if (kind is FiscalRuleKinds.PurchaseItbis or FiscalRuleKinds.SalesItbis)
        {
            var effect = RequiredString(root, "effect");
            if (kind == FiscalRuleKinds.PurchaseItbis && effect is not (TaxEffects.RecoverableInput or TaxEffects.NonRecoverableInput))
            {
                throw Invalid("effect must be RECOVERABLE_INPUT or NON_RECOVERABLE_INPUT.");
            }

            if (kind == FiscalRuleKinds.SalesItbis && effect != TaxEffects.Output)
            {
                throw Invalid("effect of SALES_ITBIS must be OUTPUT.");
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

        string? isrType = null;
        if (root.TryGetProperty("isr_withholding_type", out var type))
        {
            isrType = type.ValueKind == JsonValueKind.String && type.GetString() is { } t && t.Length == 1 && t[0] is >= '1' and <= '9'
                ? t
                : throw Invalid("isr_withholding_type is one of \"1\"…\"9\" (the 606's ISR withholding types).");
        }

        IReadOnlySet<string>? appliesTo = null;
        if (root.TryGetProperty("applies_to", out var scopes))
        {
            appliesTo = StringSet(scopes, "applies_to", TaxLineScopes.All);
            if (appliesTo.Count == 0)
            {
                throw Invalid("applies_to cannot be empty; leave it out to withhold on every line.");
            }
        }

        IReadOnlySet<string>? series = null;
        if (root.TryGetProperty("document_series", out var seriesList))
        {
            series = StringSet(seriesList, "document_series", SupplierDocumentSeries);
            if (series.Count == 0)
            {
                throw Invalid("document_series cannot be empty; leave it out to withhold on both B-series and e-CF invoices.");
            }
        }

        return new FiscalRuleDefinition(kind, taxCode, rate, TaxEffects.Withholding, new HashSet<string>(StringComparer.Ordinal), @base, parties, isrType, AppliesTo: appliesTo, DocumentSeries: series);
    }

    /// <summary>E-GAS-02-1: the label shown to whoever registers and the components, each with its own tax code.</summary>
    private static FiscalRuleDefinition ParseTaxType(string kind, JsonElement root)
    {
        var label = RequiredString(root, "label").Trim();
        if (label.Length is 0 or > 60)
        {
            throw Invalid("label has 1 to 60 characters.");
        }

        if (!root.TryGetProperty("components", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("components is required: an array (empty for an exempt type).");
        }

        var components = new List<TaxComponent>();
        foreach (var element in list.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("components must contain only objects.");
            }

            foreach (var property in element.EnumerateObject().Where(p => !ComponentKeys.Contains(p.Name)))
            {
                throw Invalid($"Unknown key '{property.Name}' in a component.");
            }

            var effect = RequiredString(element, "effect");
            if (!ComponentEffects.Contains(effect))
            {
                throw Invalid("effect of a component is RECOVERABLE_INPUT, SELECTIVE_TAX, OTHER_TAX or LEGAL_TIP.");
            }

            var component = new TaxComponent(TaxCodeOf(element), RateOf(element), effect);
            if (components.Any(c => c.TaxCode == component.TaxCode))
            {
                throw Invalid($"components: tax_code '{component.TaxCode}' is repeated.");
            }

            components.Add(component);
        }

        return new FiscalRuleDefinition(kind, kind, 0m, kind, new HashSet<string>(StringComparer.Ordinal), null, new HashSet<string>(StringComparer.Ordinal), Label: label, Components: components);
    }

    private static string TaxCodeOf(JsonElement root)
    {
        var taxCode = RequiredString(root, "tax_code");
        return System.Text.RegularExpressions.Regex.IsMatch(taxCode, "^[A-Z][A-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
            ? taxCode
            : throw Invalid("tax_code must be uppercase letters, digits and underscores.");
    }

    private static decimal RateOf(JsonElement root)
        => decimal.TryParse(RequiredString(root, "rate"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) && rate > 0 && rate <= 1 && decimal.Round(rate, 6) == rate
            ? rate
            : throw Invalid("rate must be a decimal string greater than 0 and at most 1, with at most 6 decimals.");

    /// <summary>E-FIS2-01-2: every raw-material category mapped to a 606 goods-and-services code "01"…"11".</summary>
    private static Dictionary<string, string> ParseClasses(JsonElement root)
    {
        if (!root.TryGetProperty("classes", out var classes) || classes.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("classes is required: an object from each raw-material category to a 606 code \"01\"…\"11\".");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in classes.EnumerateObject())
        {
            if (!RawMaterialCategories.Contains(entry.Name))
            {
                throw Invalid($"classes: '{entry.Name}' is not one of {string.Join(", ", RawMaterialCategories)}.");
            }

            var code = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
            if (code is null || code.Length != 2 || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 11)
            {
                throw Invalid($"classes: the code of {entry.Name} is \"01\"…\"11\".");
            }

            map[entry.Name] = code;
        }

        var missing = RawMaterialCategories.Where(c => !map.ContainsKey(c)).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0 ? map : throw Invalid("classes must cover every raw-material category; missing: " + string.Join(", ", missing));
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
