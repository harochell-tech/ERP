using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rochell.Platform.Json;

/// <summary>
/// JSON of the HTTP API and of query results: camelCase, unknown members rejected, decimals as strings (ADR-015, E-PR02-3)
/// and timestamps with an explicit offset, converted to UTC.
/// </summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Applies the API conventions to options owned by someone else (the web host).</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new DecimalStringConverter());
        options.Converters.Add(new UtcDateTimeConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>ADR-015: a decimal travels as a JSON string ("40000.00"); a JSON number is rejected so no client ever rounds it.</summary>
public sealed class DecimalStringConverter : JsonConverter<decimal>
{
    private const NumberStyles Style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Decimal values must be JSON strings (e.g. \"40000.00\").");
        }

        return decimal.TryParse(reader.GetString(), Style, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException("Invalid decimal value.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Timestamps need an explicit offset ("Z" or "-04:00") and are converted to UTC (the platform rejects other kinds);
/// they are written as UTC with microsecond precision, like PostgreSQL stores them.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            || !HasOffset(text))
        {
            throw new JsonException("Timestamps must be ISO 8601 strings with an offset (e.g. \"2026-09-24T14:30:00Z\").");
        }

        return value.UtcDateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var utc = value.Kind == DateTimeKind.Utc ? value : throw new JsonException("Only UTC timestamps are written.");
        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    }

    private static bool HasOffset(string text)
    {
        var time = text.IndexOf('T', StringComparison.Ordinal);
        if (time < 0)
        {
            return false;
        }

        var tail = text[time..];
        return tail.EndsWith('Z') || tail.EndsWith('z') || tail.Contains('+', StringComparison.Ordinal) || tail.Contains('-', StringComparison.Ordinal);
    }
}
