using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rochell.Platform.Hashing;

/// <summary>
/// RFC 8785 (JCS) canonical JSON restricted by ADR-015: numbers may only be integers with |n| ≤ 2^53−1
/// (their JCS form is the plain integer). Decimal amounts, quantities and rates must travel as strings.
/// Also rejected: duplicate property names and U+0000 (PostgreSQL jsonb cannot store it).
/// </summary>
public static class JsonCanonicalizer
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var builder = new StringBuilder(json.Length);
        Write(document.RootElement, builder);
        return builder.ToString();
    }

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, output);
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    Write(item, output);
                    first = false;
                }

                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, output);
                break;
            case JsonValueKind.Number:
                output.Append(CanonicalInteger(element.GetRawText()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder output)
    {
        var properties = new List<JsonProperty>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException($"Duplicate JSON property name '{property.Name}'.");
            }

            properties.Add(property);
        }

        // JCS: properties sorted by their UTF-16 code units.
        properties.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        output.Append('{');
        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0)
            {
                output.Append(',');
            }

            WriteString(properties[i].Name, output);
            output.Append(':');
            Write(properties[i].Value, output);
        }

        output.Append('}');
    }

    private static string CanonicalInteger(string raw)
    {
        var digits = raw.StartsWith('-') ? raw.AsSpan(1) : raw.AsSpan();
        var isInteger = digits.Length > 0 && (digits is "0" || (digits[0] != '0' && digits.IndexOfAnyExceptInRange('0', '9') < 0));
        if (!isInteger
            || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || value > MaxSafeInteger
            || value < -MaxSafeInteger)
        {
            throw new JsonException(
                $"JSON number '{raw}' is not allowed: only integers within ±(2^53−1). Decimals must be strings (ADR-015).");
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\0':
                    throw new JsonException("U+0000 is not allowed in JSON strings (not storable in PostgreSQL jsonb).");
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}
