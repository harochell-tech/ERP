using System.Text;

namespace Rochell.Treasury.Statements;

/// <summary>One movement of the file; <see cref="Row"/> is its 1-based row in the file, <see cref="Occurrence"/> its IDM-04 ordinal.</summary>
public sealed record StatementLine(int Row, DateOnly ValueDate, string Direction, decimal Amount, string? Reference, string Description, int Occurrence);

/// <summary>A row the importer could not read (E-VS2-05-9: one is enough to reject the whole file).</summary>
public sealed record StatementRowError(int Row, string Message);

/// <summary>What the file says; the statement-level values are null when the format has no cell for them.</summary>
public sealed record ParsedStatement(
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    IReadOnlyList<StatementLine> Lines,
    IReadOnlyList<StatementRowError> Errors);

public static class StatementFile
{
    public const string Debit = "DEBIT";
    public const string Credit = "CREDIT";

    public static ParsedStatement Parse(byte[] content, StatementFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(format);
        var errors = new List<StatementRowError>();
        string text;
        try
        {
            text = format.Encoding.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return new ParsedStatement(null, null, null, null, [], [new StatementRowError(0, $"The file is not valid {format.Encoding.WebName}.")]);
        }

        var rows = Rows(text.TrimStart('﻿'), format.Delimiter);
        // Blank rows at the end are not part of the bank's footer (skip_trailing_rows counts from the last written row).
        while (rows.Count > 0 && !rows[^1].Quoted && rows[^1].Fields.All(string.IsNullOrWhiteSpace))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        foreach (var row in rows.Where(r => r.Unterminated))
        {
            errors.Add(new StatementRowError(row.Number, "A quoted field is not closed."));
        }

        DateOnly? Date(string name)
        {
            if (!format.Cells.TryGetValue(name, out var cell))
            {
                return null;
            }

            if (Cell(rows, cell) is { } value && format.TryParseDate(value, out var date))
            {
                return date;
            }

            errors.Add(new StatementRowError(cell.Row + 1, $"{name} is not a date in the format {format.DateFormat}."));
            return null;
        }

        decimal? Balance(string name)
        {
            if (!format.Cells.TryGetValue(name, out var cell))
            {
                return null;
            }

            if (Cell(rows, cell) is { } value && format.TryParseAmount(value, out var amount) && amount == decimal.Round(amount, 2))
            {
                return amount;
            }

            errors.Add(new StatementRowError(cell.Row + 1, $"{name} is not an amount with at most 2 decimals."));
            return null;
        }

        var periodFrom = Date("period_from");
        var periodTo = Date("period_to");
        var opening = Balance("opening_balance");
        var closing = Balance("closing_balance");

        var lines = new List<StatementLine>();
        var occurrences = new Dictionary<(DateOnly, string, decimal, string?), int>();
        var last = rows.Count - format.SkipTrailingRows;
        for (var i = format.SkipRows; i < last; i++)
        {
            var row = rows[i];
            if (row.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (ReadLine(row, format, errors) is { } line)
            {
                var key = (line.ValueDate, line.Direction, line.Amount, line.Reference);
                var occurrence = occurrences.GetValueOrDefault(key) + 1;
                occurrences[key] = occurrence;
                lines.Add(line with { Occurrence = occurrence });
            }
        }

        return new ParsedStatement(periodFrom, periodTo, opening, closing, lines, errors);
    }

    private static StatementLine? ReadLine(CsvRow row, StatementFormat format, List<StatementRowError> errors)
    {
        var problems = new List<string>();
        string Field(int column)
        {
            if (column < row.Fields.Count)
            {
                return row.Fields[column].Trim();
            }

            problems.Add($"column {column + 1} is missing");
            return string.Empty;
        }

        var dateText = Field(format.ValueDateColumn);
        if (!format.TryParseDate(dateText, out var date))
        {
            problems.Add($"\"{dateText}\" is not a date in the format {format.DateFormat}");
        }

        var reference = format.ReferenceColumn is { } r && Field(r) is { Length: > 0 } rf ? rf : null;
        var description = Field(format.DescriptionColumn);
        if (description.Length == 0)
        {
            problems.Add("the description is empty");
        }

        var (direction, amount) = Amount(row, format, Field, problems);
        if (amount is { } a && a != decimal.Round(a, 2))
        {
            problems.Add($"{a} has more than 2 decimals");
        }

        if (problems.Count > 0)
        {
            errors.Add(new StatementRowError(row.Number, string.Join("; ", problems)));
            return null;
        }

        return new StatementLine(row.Number, date, direction!, amount!.Value, reference, description, 0);
    }

    private static (string? Direction, decimal? Amount) Amount(CsvRow row, StatementFormat format, Func<int, string> field, List<string> problems)
    {
        decimal? Number(int column)
        {
            var text = field(column);
            if (text.Length == 0)
            {
                return null;
            }

            if (format.TryParseAmount(text, out var value))
            {
                return value;
            }

            problems.Add($"\"{text}\" is not an amount");
            return null;
        }

        if (format.DebitColumn is { } debitColumn && format.CreditColumn is { } creditColumn)
        {
            var debit = Number(debitColumn) ?? 0m;
            var credit = Number(creditColumn) ?? 0m;
            if (debit < 0m || credit < 0m)
            {
                problems.Add("debit and credit amounts cannot be negative");
                return (null, null);
            }

            if ((debit != 0m) == (credit != 0m))
            {
                problems.Add(debit == 0m ? "the amount is zero or missing" : "both debit and credit are filled");
                return (null, null);
            }

            return debit != 0m ? (Debit, debit) : (Credit, credit);
        }

        var amount = Number(format.AmountColumn!.Value);
        if (amount is null || amount == 0m)
        {
            problems.Add("the amount is zero or missing");
            return (null, null);
        }

        if (format.DirectionColumn is { } directionColumn)
        {
            var indicator = field(directionColumn);
            var direction = format.DebitValues.Contains(indicator) ? Debit : format.CreditValues.Contains(indicator) ? Credit : null;
            if (direction is null || amount < 0m)
            {
                problems.Add(direction is null ? $"\"{indicator}\" is not a debit or credit indicator" : "the amount must be positive when a direction column is used");
                return (null, null);
            }

            return (direction, amount);
        }

        // A signed amount: money leaving the account is negative.
        return amount < 0m ? (Debit, -amount.Value) : (Credit, amount);
    }

    private static string? Cell(List<CsvRow> rows, FormatCell cell)
        => cell.Row < rows.Count && cell.Column < rows[cell.Row].Fields.Count ? rows[cell.Row].Fields[cell.Column].Trim() : null;

    private sealed record CsvRow(int Number, List<string> Fields, bool Quoted, bool Unterminated);

    /// <summary>RFC 4180 rows: quoted fields may hold the delimiter, doubled quotes and line breaks.</summary>
    private static List<CsvRow> Rows(string text, char delimiter)
    {
        var rows = new List<CsvRow>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        var number = 1;
        var start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (ch == '\n')
                    {
                        number++;
                    }

                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                inQuotes = true;
                quoted = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                rows.Add(new CsvRow(start, fields, quoted, false));
                fields = [];
                field.Clear();
                quoted = false;
                number++;
                start = number;
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || fields.Count > 0 || inQuotes)
        {
            fields.Add(field.ToString());
            rows.Add(new CsvRow(start, fields, quoted, inQuotes));
        }

        return rows;
    }
}
