using System.Text.Json;

namespace Rochell.Finance.Posting;

/// <summary>One line of a declarative posting rule (E-PR05-2).</summary>
public sealed record RuleLine(string Code, string Side, string AccountRole, string Amount, IReadOnlyList<string> Dimensions, string? Subledger)
{
    public bool IsDebit => Side == RuleDefinition.Debit;
}

/// <summary>
/// Declarative posting rule: {"lines":[{"code","side":"DEBIT|CREDIT","account_role","amount","dimensions":["plant","item","party"],"subledger":"INV|AP"?}]}.
/// Code only computes the named amounts; accounts come from the account-role map.
/// </summary>
public sealed record RuleDefinition(IReadOnlyList<RuleLine> Lines)
{
    public const string Debit = "DEBIT";
    public const string Credit = "CREDIT";
    public static readonly IReadOnlySet<string> AllowedDimensions = new HashSet<string>(StringComparer.Ordinal) { "plant", "item", "party" };
    public static readonly IReadOnlySet<string> AllowedSubledgers = new HashSet<string>(StringComparer.Ordinal) { "INV", "AP" };

    /// <summary>Parses and validates the structure. Role existence and control consistency are checked against the database on approval.</summary>
    public static RuleDefinition Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out var linesElement) || linesElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The rule definition must contain a \"lines\" array.");
        }

        var lines = new List<RuleLine>();
        foreach (var line in linesElement.EnumerateArray())
        {
            var dimensions = line.TryGetProperty("dimensions", out var dims) && dims.ValueKind == JsonValueKind.Array
                ? dims.EnumerateArray().Select(d => d.GetString() ?? string.Empty).ToList()
                : [];
            var subledger = line.TryGetProperty("subledger", out var sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null;
            lines.Add(new RuleLine(Required(line, "code"), Required(line, "side"), Required(line, "account_role"), Required(line, "amount"), dimensions, subledger));
        }

        var definition = new RuleDefinition(lines);
        definition.Validate();
        return definition;
    }

    public RuleLine Line(string code)
        => Lines.FirstOrDefault(l => l.Code == code) ?? throw new InvalidOperationException($"Posting rule has no line '{code}'.");

    private void Validate()
    {
        if (Lines.Count == 0 || !Lines.Any(l => l.Side == Debit) || !Lines.Any(l => l.Side == Credit))
        {
            throw new FormatException("A posting rule needs at least one DEBIT and one CREDIT line.");
        }

        if (Lines.Select(l => l.Code).Distinct(StringComparer.Ordinal).Count() != Lines.Count)
        {
            throw new FormatException("Line codes must be unique.");
        }

        foreach (var line in Lines)
        {
            if (line.Side is not (Debit or Credit))
            {
                throw new FormatException($"Line {line.Code}: side must be DEBIT or CREDIT.");
            }

            if (line.Dimensions.Any(d => !AllowedDimensions.Contains(d)) || line.Dimensions.Distinct(StringComparer.Ordinal).Count() != line.Dimensions.Count)
            {
                throw new FormatException($"Line {line.Code}: dimensions must be distinct values of plant, item, party.");
            }

            if (line.Subledger is not null && !AllowedSubledgers.Contains(line.Subledger))
            {
                throw new FormatException($"Line {line.Code}: subledger must be INV or AP.");
            }
        }
    }

    private static string Required(JsonElement line, string property)
        => line.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"Every line needs a non-empty \"{property}\".");
}
