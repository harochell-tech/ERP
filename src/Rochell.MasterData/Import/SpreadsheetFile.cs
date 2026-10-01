using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Rochell.MasterData.Import;

public sealed class SpreadsheetException(string message) : Exception(message);

/// <summary>
/// E-IMP-01-1: the first sheet of an .xlsx workbook, or a .csv file (UTF-8; comma or semicolon), as rows of trimmed text.
/// Only what a master-data export needs: shared and inline strings, numbers as written, no formulas evaluated.
/// </summary>
public static class SpreadsheetFile
{
    public const int MaxRows = 2001;
    public const int MaxColumns = 50;

    /// <summary>What one part of the workbook may hold once decompressed.</summary>
    private const int MaxPartBytes = 32 * 1024 * 1024;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static IReadOnlyList<IReadOnlyList<string>> Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var rows = content.Length >= 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4 ? ReadWorkbook(content) : ReadCsv(content);
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static List<IReadOnlyList<string>> ReadWorkbook(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var shared = SharedStrings(archive);
            var sheet = Load(archive, FirstSheetPath(archive)) ?? throw new SpreadsheetException("The workbook has no sheet.");
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in sheet.Descendants(Main + "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = ColumnIndex((string?)cell.Attribute("r"), cells.Count);
                    if (column >= MaxColumns)
                    {
                        throw new SpreadsheetException($"The sheet has more than {MaxColumns} columns.");
                    }

                    while (cells.Count < column)
                    {
                        cells.Add(string.Empty);
                    }

                    cells.Add(Clean(CellText(cell, shared)));
                }

                // Rows the file skips (an r attribute ahead of the count) are empty rows.
                var number = int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : rows.Count + 1;
                while (rows.Count < number - 1 && rows.Count < MaxRows)
                {
                    rows.Add([]);
                }

                rows.Add(cells);
                if (rows.Count > MaxRows)
                {
                    throw new SpreadsheetException($"The sheet has more than {MaxRows - 1} data rows.");
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException)
        {
            throw new SpreadsheetException("The file is not a readable .xlsx workbook.");
        }
    }

    private static List<string> SharedStrings(ZipArchive archive)
        => Load(archive, "xl/sharedStrings.xml") is { } document ? [.. document.Root!.Elements(Main + "si").Select(Text)] : [];

    private static string FirstSheetPath(ZipArchive archive)
    {
        var workbook = Load(archive, "xl/workbook.xml") ?? throw new SpreadsheetException("The file is not a readable .xlsx workbook.");
        var id = (string?)workbook.Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Relationships + "id");
        var target = (string?)Load(archive, "xl/_rels/workbook.xml.rels")?.Root!.Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(e => (string?)e.Attribute("Id") == id)?.Attribute("Target");
        if (string.IsNullOrEmpty(target))
        {
            return "xl/worksheets/sheet1.xml";
        }

        return target.StartsWith('/') ? target[1..] : "xl/" + target;
    }

    private static XDocument? Load(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        // The declared size can lie: read through a cap.
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxPartBytes)
            {
                throw new SpreadsheetException("The workbook is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    /// <summary>The text of a shared or inline string: its runs, without the phonetic ones.</summary>
    private static string Text(XElement item)
        => string.Concat(item.Descendants(Main + "t").Where(t => t.Ancestors(Main + "rPh").FirstOrDefault() is null).Select(t => t.Value));

    private static string CellText(XElement cell, List<string> shared)
    {
        var value = (string?)cell.Element(Main + "v");
        switch ((string?)cell.Attribute("t"))
        {
            case "s":
                return value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < shared.Count ? shared[index] : string.Empty;
            case "inlineStr":
                return cell.Element(Main + "is") is { } inline ? Text(inline) : string.Empty;
            case "str" or "b" or "e":
                return value ?? string.Empty;
            default:
                // A number as the sheet stores it; "1.19020087E8" is the identifier 119020087.
                return value is not null && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString("0.############################", CultureInfo.InvariantCulture)
                    : value ?? string.Empty;
        }
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return fallback;
        }

        var index = 0;
        foreach (var c in reference.TakeWhile(char.IsAsciiLetter))
        {
            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index == 0 ? fallback : index - 1;
    }

    private static List<IReadOnlyList<string>> ReadCsv(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (ArgumentException)
        {
            throw new SpreadsheetException("The file is neither an .xlsx workbook nor UTF-8 text.");
        }

        text = text.TrimStart('﻿');
        var header = text.Split('\n', 2)[0];
        var delimiter = header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
        var rows = new List<IReadOnlyList<string>>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                EndCell();
            }
            else if (c == '\n')
            {
                EndRow();
            }
            else if (c != '\r')
            {
                cell.Append(c);
            }
        }

        if (quoted)
        {
            throw new SpreadsheetException("The CSV file has an unclosed quote.");
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            EndRow();
        }

        return rows;

        void EndCell()
        {
            if (cells.Count >= MaxColumns)
            {
                throw new SpreadsheetException($"The file has more than {MaxColumns} columns.");
            }

            cells.Add(Clean(cell.ToString()));
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            rows.Add(cells);
            cells = [];
            if (rows.Count > MaxRows)
            {
                throw new SpreadsheetException($"The file has more than {MaxRows - 1} data rows.");
            }
        }
    }

    /// <summary>Trimmed, with tabs, line breaks and non-breaking spaces as plain spaces.</summary>
    private static string Clean(string value)
        => string.Join(' ', value.Split(['\t', '\r', '\n', ' ', ' '], StringSplitOptions.RemoveEmptyEntries));
}
