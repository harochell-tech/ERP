using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.MasterData.Import;

/// <summary>
/// One row of a supplier or customer file. <see cref="Outcome"/>: CREATE (a new party), LINK (the supplier with that RNC also
/// becomes a customer), EXISTS, DUPLICATE (the RNC appears earlier in the file) or REJECTED; <see cref="ReasonCode"/> says why a
/// row is not loaded. <see cref="LegalName"/> is the name the party gets: the DGII registry's when the RNC is in it (E-IMP-3).
/// <see cref="CreditLimit"/> is text with two decimals (ADR-015).
/// </summary>
public sealed record PartyImportRow(
    int Row,
    string Name,
    string? Rnc,
    string? LegalName,
    string? RegistryName,
    string? RegistryStatus,
    bool NameDiffers,
    string? Phone,
    IReadOnlyList<string> Emails,
    int? PaymentTermsDays,
    string? CreditLimit,
    string Outcome,
    string? ReasonCode,
    string? Reason,
    Guid? PartyId);

public sealed record PartyImportPreview(
    string FileName,
    string Sha256,
    int Rows,
    int ToCreate,
    int ToLink,
    int Existing,
    int Duplicates,
    int Rejected,
    IReadOnlyList<string> IgnoredColumns,
    IReadOnlyList<PartyImportRow> Items);

/// <summary>The body of the preview queries and the import commands: the file as the browser read it (E-IMP-01-1).</summary>
public sealed record PartyImportRequest(string FileName, string ContentBase64);

/// <summary>
/// E-IMP-1…11: reads the ADM Cloud export of suppliers or customers and decides, row by row, what loading it would do. The
/// preview queries and the import commands share this analysis, so the screen shows exactly what the command then writes.
/// </summary>
public static partial class PartyImport
{
    /// <summary>E-IMP-01-1: as the bank statements, the decoded file is at most 5 MB.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    public const string Create = "CREATE";
    public const string Link = "LINK";
    public const string Exists = "EXISTS";
    public const string Duplicate = "DUPLICATE";
    public const string Rejected = "REJECTED";

    public sealed record ParsedFile(string FileName, string Sha256, IReadOnlyList<string> IgnoredColumns, IReadOnlyList<ParsedRow> Rows);

    public sealed record ParsedRow(int Row, string Name, string? Rnc, string? Phone, IReadOnlyList<string> Emails, int? PaymentTermsDays, decimal? CreditLimit, string? ReasonCode, string? Reason);

    private sealed record Existing(Guid PartyId, string LegalName, bool IsSupplier, bool IsCustomer);

    private sealed record Registered(string LegalName, string Status);

    [GeneratedRegex("^([0-9]{1,3}) d[ií]as?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Days();

    /// <summary>Decodes and reads the file; refuses one that is not the expected export (the rows are judged one by one).</summary>
    public static ParsedFile Parse(string? fileName, string? contentBase64, bool customers)
    {
        var name = (fileName ?? string.Empty).Trim();
        if (name.Length is 0 or > 255)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, "The file name must have 1 to 255 characters.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(contentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, "The file content is not valid base64.");
        }

        if (content.Length == 0)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, "The file is empty.");
        }

        if (content.Length > MaxFileBytes)
        {
            throw new DomainException(MasterDataErrors.ImportFileTooLarge, $"The file has {content.Length} bytes; the limit is {MaxFileBytes} (E-IMP-01-1).");
        }

        IReadOnlyList<IReadOnlyList<string>> sheet;
        try
        {
            sheet = SpreadsheetFile.Read(content);
        }
        catch (SpreadsheetException ex)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, ex.Message);
        }

        if (sheet.Count < 2)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, "The file needs a header row and at least one data row.");
        }

        var header = sheet[0];
        int Column(params string[] names) => Enumerable.Range(0, header.Count).FirstOrDefault(i => names.Contains(Key(header[i])), -1);
        var nameColumn = Column("razon social");
        var rncColumn = Column("id fiscal");
        if (nameColumn < 0 || rncColumn < 0)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, "The first row must have the columns \"Razón Social\" and \"ID Fiscal\" (E-IMP-1).");
        }

        var phoneColumn = Column("telefono 1", "telefono");
        var emailColumn = Column("correo electronico", "correo");
        var termsColumn = Column("termino de pago");
        var limitColumn = customers ? Column("limite de credito") : -1;
        int[] used = [nameColumn, rncColumn, phoneColumn, emailColumn, termsColumn, limitColumn];
        var ignored = Enumerable.Range(0, header.Count).Where(i => !used.Contains(i) && header[i].Length > 0).Select(i => header[i]).ToList();

        var rows = new List<ParsedRow>();
        for (var i = 1; i < sheet.Count; i++)
        {
            var cells = sheet[i];
            string Cell(int column) => column >= 0 && column < cells.Count ? cells[column] : string.Empty;
            if (cells.All(string.IsNullOrEmpty))
            {
                continue;
            }

            rows.Add(Row(i + 1, Cell(nameColumn), Cell(rncColumn), Cell(phoneColumn), Cell(emailColumn), Cell(termsColumn), Cell(limitColumn)));
        }

        if (rows.Count == 0)
        {
            throw new DomainException(MasterDataErrors.ImportFileInvalid, "The file has no data rows.");
        }

        return new ParsedFile(name, Convert.ToHexStringLower(SHA256.HashData(content)), ignored, rows);
    }

    private static ParsedRow Row(int number, string name, string rnc, string phone, string email, string terms, string limit)
    {
        ParsedRow Refused(string code, string reason) => new(number, name, null, null, [], null, null, code, reason);

        if (name.Length is 0 or > 200)
        {
            return Refused("NAME_INVALID", "The legal name has 1 to 200 characters.");
        }

        if (rnc.Length == 0)
        {
            return Refused("ID_MISSING", "The row has no fiscal identifier (E-IMP-4).");
        }

        var digits = new string([.. rnc.Where(c => c is not ('-' or ' '))]);
        if (digits.Length is not (9 or 11) || !digits.All(char.IsAsciiDigit))
        {
            return Refused("ID_INVALID", "The fiscal identifier must have 9 digits (RNC) or 11 (cédula) (E-IMP-4).");
        }

        if (phone.Length > 30)
        {
            return Refused("PHONE_INVALID", "The phone has at most 30 characters.");
        }

        var (emails, emailError) = PartyEmails.Normalize(PartyEmails.Split(email));
        if (emailError is not null)
        {
            return Refused("EMAIL_INVALID", emailError);
        }

        int? days = null;
        if (terms.Length > 0)
        {
            if (string.Equals(terms, "Contado", StringComparison.OrdinalIgnoreCase))
            {
                days = 0;
            }
            else if (Days().Match(terms) is { Success: true } match && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) <= 365)
            {
                days = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                return Refused("TERMS_INVALID", $"The payment term '{terms}' is not \"Contado\" or \"N días\" up to 365 (E-IMP-2).");
            }
        }

        decimal? creditLimit = null;
        if (limit.Length > 0)
        {
            if (!decimal.TryParse(limit, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0m || decimal.Round(amount, 2) != amount)
            {
                return Refused("CREDIT_LIMIT_INVALID", "The credit limit is zero or more, with at most 2 decimals (E-IMP-8).");
            }

            creditLimit = amount;
        }

        return new ParsedRow(number, name, digits, phone.Length == 0 ? null : phone, emails, days, creditLimit, null, null);
    }

    /// <summary>What loading the file would do in this company now (E-IMP-3, E-IMP-4, E-IMP-5).</summary>
    public static async Task<PartyImportPreview> AnalyzeAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId, ParsedFile file, bool customers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var rncs = file.Rows.Where(r => r.Rnc is not null).Select(r => r.Rnc!).Distinct().ToArray();
        var existing = (await Reading.ListAsync(
            connection,
            transaction,
            "SELECT rnc, party_id, legal_name, is_supplier, is_customer FROM md.party WHERE company_id = @c AND rnc = ANY(@rncs)",
            r => (Rnc: r.GetString(0), Party: new Existing(r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4))),
            cancellationToken,
            ("c", companyId),
            ("rncs", rncs)).ConfigureAwait(false)).ToDictionary(x => x.Rnc, x => x.Party);
        var registry = (await Reading.ListAsync(
            connection,
            transaction,
            "SELECT rnc, legal_name, status FROM md.rnc_registry WHERE rnc = ANY(@rncs)",
            r => (Rnc: r.GetString(0), Entry: new Registered(r.GetString(1), r.GetString(2))),
            cancellationToken,
            ("rncs", rncs)).ConfigureAwait(false)).ToDictionary(x => x.Rnc, x => x.Entry);

        var seen = new HashSet<string>();
        var items = new List<PartyImportRow>();
        foreach (var row in file.Rows)
        {
            if (row.Rnc is null)
            {
                items.Add(new PartyImportRow(row.Row, row.Name, null, null, null, null, false, null, [], null, null, Rejected, row.ReasonCode, row.Reason, null));
                continue;
            }

            registry.TryGetValue(row.Rnc, out var registered);
            existing.TryGetValue(row.Rnc, out var party);
            var (outcome, code, reason) = Decide(row.Rnc, party, customers, firstInFile: seen.Add(row.Rnc));
            var legalName = outcome == Link || outcome == Exists ? party!.LegalName : registered?.LegalName ?? row.Name;
            items.Add(new PartyImportRow(
                row.Row,
                row.Name,
                row.Rnc,
                legalName,
                registered?.LegalName,
                registered?.Status,
                registered is not null && Key(registered.LegalName, lettersOnly: true) != Key(row.Name, lettersOnly: true),
                row.Phone,
                row.Emails,
                row.PaymentTermsDays,
                customers ? (row.CreditLimit ?? 0m).ToString("0.00", CultureInfo.InvariantCulture) : null,
                outcome,
                code,
                reason,
                party?.PartyId));
        }

        return new PartyImportPreview(
            file.FileName,
            file.Sha256,
            items.Count,
            items.Count(i => i.Outcome == Create),
            items.Count(i => i.Outcome == Link),
            items.Count(i => i.Outcome == Exists),
            items.Count(i => i.Outcome == Duplicate),
            items.Count(i => i.Outcome == Rejected),
            file.IgnoredColumns,
            items);
    }

    private static (string Outcome, string? Code, string? Reason) Decide(string rnc, Existing? party, bool customers, bool firstInFile)
    {
        if (!firstInFile)
        {
            return (Duplicate, "DUPLICATE_IN_FILE", $"RNC {rnc} appears earlier in the file (E-IMP-5).");
        }

        if (party is null)
        {
            return (Create, null, null);
        }

        if (customers)
        {
            return party.IsCustomer ? (Exists, "ALREADY_CUSTOMER", $"RNC {rnc} is already a customer (E-IMP-5).") : (Link, null, null);
        }

        return party.IsSupplier
            ? (Exists, "ALREADY_SUPPLIER", $"RNC {rnc} is already a supplier (E-IMP-5).")
            : (Exists, "CUSTOMER_ONLY", $"RNC {rnc} exists as a customer; the import does not make it a supplier.");
    }

    /// <summary>
    /// Lower case without Spanish accents; <paramref name="lettersOnly"/> also drops everything but letters and digits. The host
    /// runs with invariant globalization, so the accents are folded here and not by Unicode normalization.
    /// </summary>
    private static string Key(string value, bool lettersOnly = false)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var folded = c switch
            {
                'á' or 'Á' or 'à' or 'À' or 'ä' or 'Ä' => 'a',
                'é' or 'É' or 'è' or 'È' or 'ë' or 'Ë' => 'e',
                'í' or 'Í' or 'ì' or 'Ì' or 'ï' or 'Ï' => 'i',
                'ó' or 'Ó' or 'ò' or 'Ò' or 'ö' or 'Ö' => 'o',
                'ú' or 'Ú' or 'ù' or 'Ù' or 'ü' or 'Ü' => 'u',
                'ñ' or 'Ñ' => 'n',
                _ => char.ToLowerInvariant(c),
            };
            if (!lettersOnly || char.IsAsciiLetterOrDigit(folded))
            {
                builder.Append(folded);
            }
        }

        return builder.ToString().Trim();
    }
}
