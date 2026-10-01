using System.IO.Compression;
using System.Security;
using System.Text;

namespace Rochell.TestInfrastructure;

/// <summary>A one-sheet .xlsx workbook with shared strings, as ADM Cloud exports them (E-IMP-01-1); synthetic rows only (E-IMP-11).</summary>
public static class TestSpreadsheet
{
    public static byte[] Xlsx(params string?[][] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var strings = new List<string>();
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append("<row r=\"").Append(r + 1).Append("\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is not { } value)
                {
                    continue; // an empty cell is absent from the file
                }

                var index = strings.IndexOf(value);
                if (index < 0)
                {
                    strings.Add(value);
                    index = strings.Count - 1;
                }

                sheet.Append("<c r=\"").Append((char)('A' + c)).Append(r + 1).Append("\" t=\"s\"><v>").Append(index).Append("</v></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        var shared = "<?xml version=\"1.0\" encoding=\"utf-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + string.Concat(strings.Select(s => "<si><t xml:space=\"preserve\">" + SecurityElement.Escape(s) + "</t></si>")) + "</sst>";
        const string Workbook = "<?xml version=\"1.0\" encoding=\"utf-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>";
        const string Rels = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>";

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "xl/workbook.xml", Workbook);
            Add(archive, "xl/_rels/workbook.xml.rels", Rels);
            Add(archive, "xl/sharedStrings.xml", shared);
            Add(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return buffer.ToArray();
    }

    public static string Base64(params string?[][] rows) => Convert.ToBase64String(Xlsx(rows));

    private static void Add(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
