using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rochell.Treasury.Statements;

/// <summary>A cell of the file (0-based row over all rows of the file, 0-based column) holding a statement-level value.</summary>
public sealed record FormatCell(int Row, int Column);

/// <summary>
/// E-VS2-05-1: how one bank writes its statement CSV (<c>fin.bank_statement_format.definition</c>).
/// <code>
/// {"encoding": "UTF-8" | "ISO-8859-1" | "windows-1252", "delimiter": ",", "skip_rows": 1, "skip_trailing_rows": 0,
///  "date_format": "dd/MM/yyyy", "decimal_separator": ".", "thousands_separator": ",",
///  "columns": {"value_date": 0, "reference": 1, "description": 2,
///              "debit": 3, "credit": 4                                  -- two amount columns, one of them filled
///           or "amount": 3                                              -- signed amount, negative = DEBIT
///           or "amount": 3, "direction": 4, "debit_values": ["D"], "credit_values": ["C"]},
///  "cells": {"period_from": {"row": 0, "column": 1}, "period_to": …, "opening_balance": …, "closing_balance": …}}
/// </code>
/// "reference" and "cells" are optional; a statement-level value missing from "cells" comes from the command (E-VS2-05-3).
/// </summary>
public sealed class StatementFormat
{
    private StatementFormat()
    {
    }

    public required Encoding Encoding { get; init; }

    public required char Delimiter { get; init; }

    public required int SkipRows { get; init; }

    public required int SkipTrailingRows { get; init; }

    public required string DateFormat { get; init; }

    public required string DecimalSeparator { get; init; }

    public required string ThousandsSeparator { get; init; }

    public required int ValueDateColumn { get; init; }

    public int? ReferenceColumn { get; init; }

    public required int DescriptionColumn { get; init; }

    public int? DebitColumn { get; init; }

    public int? CreditColumn { get; init; }

    public int? AmountColumn { get; init; }

    public int? DirectionColumn { get; init; }

    public IReadOnlySet<string> DebitValues { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> CreditValues { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, FormatCell> Cells { get; init; } = new Dictionary<string, FormatCell>(StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> CellNames = new HashSet<string>(StringComparer.Ordinal) { "period_from", "period_to", "opening_balance", "closing_balance" };

    /// <summary>Parses and validates a stored definition; a bad definition is a deployment defect, reported as FormatException.</summary>
    public static StatementFormat Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var columns = Property(root, "columns");
        var debit = OptionalInt(columns, "debit");
        var credit = OptionalInt(columns, "credit");
        var amount = OptionalInt(columns, "amount");
        var direction = OptionalInt(columns, "direction");
        var split = debit is not null && credit is not null && amount is null && direction is null;
        var signed = amount is not null && debit is null && credit is null;
        if (!split && !signed)
        {
            throw new FormatException("columns: use \"debit\" and \"credit\", or \"amount\" (optionally with \"direction\").");
        }

        var debitValues = Values(columns, "debit_values");
        var creditValues = Values(columns, "credit_values");
        if (direction is not null && (debitValues.Count == 0 || creditValues.Count == 0 || debitValues.Overlaps(creditValues)))
        {
            throw new FormatException("A \"direction\" column needs distinct, non-empty \"debit_values\" and \"credit_values\".");
        }

        var delimiter = Text(root, "delimiter");
        var decimalSeparator = Text(root, "decimal_separator");
        var thousands = root.TryGetProperty("thousands_separator", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        if (delimiter.Length != 1 || decimalSeparator is not ("." or ",") || thousands is not ("" or "." or "," or " ") || thousands == decimalSeparator)
        {
            throw new FormatException("delimiter must be one character; decimal_separator \".\" or \",\"; thousands_separator \"\", \".\", \",\" or \" \", different from the decimal one.");
        }

        var cells = new Dictionary<string, FormatCell>(StringComparer.Ordinal);
        if (root.TryGetProperty("cells", out var c))
        {
            foreach (var cell in c.EnumerateObject())
            {
                if (!CellNames.Contains(cell.Name))
                {
                    throw new FormatException($"Unknown cell \"{cell.Name}\".");
                }

                cells[cell.Name] = new FormatCell(Int(cell.Value, "row"), Int(cell.Value, "column"));
            }
        }

        var format = new StatementFormat
        {
            Encoding = EncodingOf(Text(root, "encoding")),
            Delimiter = delimiter[0],
            SkipRows = OptionalInt(root, "skip_rows") ?? 0,
            SkipTrailingRows = OptionalInt(root, "skip_trailing_rows") ?? 0,
            DateFormat = Text(root, "date_format"),
            DecimalSeparator = decimalSeparator,
            ThousandsSeparator = thousands,
            ValueDateColumn = Int(columns, "value_date"),
            ReferenceColumn = OptionalInt(columns, "reference"),
            DescriptionColumn = Int(columns, "description"),
            DebitColumn = debit,
            CreditColumn = credit,
            AmountColumn = amount,
            DirectionColumn = direction,
            DebitValues = debitValues,
            CreditValues = creditValues,
            Cells = cells,
        };
        if (format.SkipRows < 0 || format.SkipTrailingRows < 0)
        {
            throw new FormatException("skip_rows and skip_trailing_rows cannot be negative.");
        }

        return format;
    }

    private static Encoding EncodingOf(string name) => name.ToUpperInvariant() switch
    {
        "UTF-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        "ISO-8859-1" => Encoding.Latin1,
        "WINDOWS-1252" => CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? throw new FormatException("windows-1252 is not available."),
        _ => throw new FormatException($"Unsupported encoding \"{name}\" (UTF-8, ISO-8859-1, windows-1252)."),
    };

    private static JsonElement Property(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : throw new FormatException($"Missing \"{name}\".");

    private static string Text(JsonElement element, string name)
        => Property(element, name).GetString() is { Length: > 0 } s ? s : throw new FormatException($"\"{name}\" must be a non-empty string.");

    private static int Int(JsonElement element, string name)
        => OptionalInt(element, name) ?? throw new FormatException($"Missing \"{name}\".");

    private static int? OptionalInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) && i >= 0
            ? i
            : throw new FormatException($"\"{name}\" must be a non-negative integer.");
    }

    private static HashSet<string> Values(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.EnumerateArray().Select(v => (v.GetString() ?? string.Empty).Trim()).Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A decimal as the bank writes it: thousands separators removed, the decimal separator turned into a point.</summary>
    public bool TryParseAmount(string text, out decimal amount)
    {
        var s = text.Trim();
        if (ThousandsSeparator.Length > 0)
        {
            s = s.Replace(ThousandsSeparator, string.Empty, StringComparison.Ordinal);
        }

        if (DecimalSeparator == ",")
        {
            s = s.Replace(",", ".", StringComparison.Ordinal);
        }

        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    public bool TryParseDate(string text, out DateOnly date)
        => DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
