using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Treasury.Payments;

namespace Rochell.Treasury.Statements;

[RequiresPermission("bank_statement:import")]
public sealed class ImportBankStatementHandler : ICommandHandler<ImportBankStatement>
{
    /// <summary>E-VS2-05-2: the decoded file is at most 5 MB (the table's CHECK is the last guard).</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>How many bad rows the rejection lists; the count is always complete.</summary>
    public const int ReportedErrors = 50;

    public const string Aggregate = "BankStatement";

    public string CommandType => "Treasury.ImportBankStatement";

    private sealed record Account(string BankCode, string Status);

    private sealed record Format(Guid FormatId, int Version, string Definition);

    private sealed record ExistingLine(Guid LineId, Guid StatementId);

    private sealed record Period(Guid PeriodId, DateOnly StartsOn, DateOnly EndsOn);

    public async Task<string> HandleAsync(ImportBankStatement command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var fileName = (command.FileName ?? string.Empty).Trim();
        if (fileName.Length is 0 or > 255)
        {
            throw new DomainException(StatementErrors.FileInvalid, "The file name must have 1 to 255 characters.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(command.ContentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new DomainException(StatementErrors.FileInvalid, "The file content is not valid base64.");
        }

        if (content.Length == 0)
        {
            throw new DomainException(StatementErrors.FileInvalid, "The file is empty.");
        }

        if (content.Length > MaxFileBytes)
        {
            throw new DomainException(StatementErrors.FileTooLarge, $"The file has {content.Length} bytes; the limit is {MaxFileBytes} (E-VS2-05-2).");
        }

        // Lock order: bank account → period × BANK-REC (as the posting engine does after the documents it locks).
        var account = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT bank_code, status FROM fin.bank_account WHERE bank_account_id = @b AND company_id = @c FOR UPDATE",
            r => new Account(r.GetString(0), r.GetString(1)),
            cancellationToken,
            ("b", command.BankAccountId),
            ("c", context.CompanyId)).ConfigureAwait(false)
            ?? throw new DomainException(StatementErrors.NotFound, "The bank account does not exist.");
        if (account.Status != "ACTIVE")
        {
            throw new DomainException(StatementErrors.BankAccountNotActive, "The bank account is closed.");
        }

        var sha256 = SHA256.HashData(content);
        var existing = await PaymentRules.ScalarAsync<Guid?>(
            context,
            "SELECT statement_id FROM fin.bank_statement WHERE company_id = @c AND bank_account_id = @b AND file_sha256 = @h",
            cancellationToken,
            ("c", context.CompanyId),
            ("b", command.BankAccountId),
            ("h", sha256)).ConfigureAwait(false);
        if (existing is { } statementId)
        {
            throw new DomainException(StatementErrors.AlreadyImported, $"This file was already imported as statement {statementId} (E-VS2-05-4).");
        }

        var format = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT format_id, version, definition::text FROM fin.bank_statement_format WHERE bank_code = @code ORDER BY version DESC LIMIT 1",
            r => new Format(r.GetGuid(0), r.GetInt32(1), r.GetString(2)),
            cancellationToken,
            ("code", account.BankCode)).ConfigureAwait(false)
            ?? throw new DomainException(StatementErrors.FormatMissing, $"There is no statement format for bank {account.BankCode} (E-VS2-05-1).");

        var parsed = StatementFile.Parse(content, StatementFormat.Parse(format.Definition));
        if (parsed.Errors.Count > 0)
        {
            var listed = string.Join("; ", parsed.Errors.Take(ReportedErrors).Select(e => $"row {e.Row}: {e.Message}"));
            var more = parsed.Errors.Count > ReportedErrors ? $"; and {parsed.Errors.Count - ReportedErrors} more" : string.Empty;
            throw new DomainException(StatementErrors.FileInvalid, $"{parsed.Errors.Count} row(s) cannot be read, nothing was imported (E-VS2-05-9): {listed}{more}.");
        }

        var periodFrom = Field("period_from", parsed.PeriodFrom, command.PeriodFrom);
        var periodTo = Field("period_to", parsed.PeriodTo, command.PeriodTo);
        var opening = Field("opening_balance", parsed.OpeningBalance, command.OpeningBalance);
        var closing = Field("closing_balance", parsed.ClosingBalance, command.ClosingBalance);
        if (opening != decimal.Round(opening, 2) || closing != decimal.Round(closing, 2))
        {
            throw new DomainException(StatementErrors.AmountInvalid, "Balances have at most 2 decimals (E-VS2-03-5).");
        }

        if (periodTo < periodFrom)
        {
            throw new DomainException(StatementErrors.FieldsDisagree, $"The period ends ({periodTo:yyyy-MM-dd}) before it starts ({periodFrom:yyyy-MM-dd}).");
        }

        var outside = parsed.Lines.Where(l => l.ValueDate < periodFrom || l.ValueDate > periodTo).ToList();
        if (outside.Count > 0)
        {
            throw new DomainException(
                StatementErrors.LineOutsidePeriod,
                $"Row(s) {string.Join(", ", outside.Select(l => l.Row))} are dated outside {periodFrom:yyyy-MM-dd}…{periodTo:yyyy-MM-dd}, nothing was imported (E-VS2-05-9).");
        }

        var credits = parsed.Lines.Where(l => l.Direction == StatementFile.Credit).Sum(l => l.Amount);
        var debits = parsed.Lines.Where(l => l.Direction == StatementFile.Debit).Sum(l => l.Amount);
        if (opening + credits - debits != closing)
        {
            throw new DomainException(
                StatementErrors.BalanceMismatch,
                $"Opening {PaymentRules.Money(opening)} + credits {PaymentRules.Money(credits)} − debits {PaymentRules.Money(debits)} = {PaymentRules.Money(opening + credits - debits)}, not the closing balance {PaymentRules.Money(closing)} (E-VS2-05-3).");
        }

        // IDM-04 (E-VS2-01-12): a line already imported from another file of this account is a duplicate, not a new line.
        var known = new Dictionary<(string, string?, decimal, DateOnly, int), ExistingLine>();
        await using (var select = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT direction, bank_reference, amount, value_date, occurrence, line_id, statement_id FROM fin.bank_statement_line
            WHERE company_id = @c AND bank_account_id = @b AND value_date BETWEEN @from AND @to
            """,
            ("c", context.CompanyId),
            ("b", command.BankAccountId),
            ("from", periodFrom),
            ("to", periodTo)))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                known[(reader.GetString(0), reader.NullableString(1), reader.GetDecimal(2), reader.Date(3), reader.GetInt32(4))] =
                    new ExistingLine(reader.GetGuid(5), reader.GetGuid(6));
            }
        }

        var duplicates = new List<(StatementLine Line, ExistingLine Existing)>();
        var inserted = new List<StatementLine>();
        foreach (var line in parsed.Lines)
        {
            if (known.TryGetValue((line.Direction, line.Reference, line.Amount, line.ValueDate, line.Occurrence), out var match))
            {
                duplicates.Add((line, match));
            }
            else
            {
                inserted.Add(line);
            }
        }

        await RequireOpenBankRecAsync(context, inserted, cancellationToken).ConfigureAwait(false);

        var statement = context.ResultRef;
        var user = await PaymentRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var now = context.Clock.UtcNow;
        var duplicatesPayload = duplicates.Select(d => new
        {
            row = d.Line.Row,
            valueDate = d.Line.ValueDate,
            direction = d.Line.Direction,
            amount = PaymentRules.Money(d.Line.Amount),
            reference = d.Line.Reference,
            existingLineId = d.Existing.LineId,
            existingStatementId = d.Existing.StatementId,
        }).ToList();
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankStatementImported",
                1,
                Aggregate,
                statement,
                1,
                JsonSerializer.Serialize(new
                {
                    statementId = statement,
                    bankAccountId = command.BankAccountId,
                    fileName,
                    fileSha256 = Convert.ToHexStringLower(sha256),
                    formatId = format.FormatId,
                    formatVersion = format.Version,
                    periodFrom,
                    periodTo,
                    openingBalance = PaymentRules.Money(opening),
                    closingBalance = PaymentRules.Money(closing),
                    linesInFile = parsed.Lines.Count,
                    inserted = inserted.Count,
                    duplicates = duplicatesPayload,
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.bank_statement (statement_id, company_id, bank_account_id, period_from, period_to, opening_balance, closing_balance, file_sha256, imported_by, imported_at)
            VALUES (@id, @c, @b, @from, @to, @opening, @closing, @h, @user, @now)
            """,
            cancellationToken,
            ("id", statement),
            ("c", context.CompanyId),
            ("b", command.BankAccountId),
            ("from", periodFrom),
            ("to", periodTo),
            ("opening", opening),
            ("closing", closing),
            ("h", sha256),
            ("user", user),
            ("now", now)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.bank_statement_file (statement_id, company_id, bank_account_id, format_id, file_name, content)
            VALUES (@id, @c, @b, @f, @name, @content)
            """,
            cancellationToken,
            ("id", statement),
            ("c", context.CompanyId),
            ("b", command.BankAccountId),
            ("f", format.FormatId),
            ("name", fileName),
            ("content", content)).ConfigureAwait(false);
        if (inserted.Count > 0)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.bank_statement_line (line_id, company_id, statement_id, bank_account_id, value_date, direction, amount, bank_reference, description, occurrence, status, version)
                SELECT l.id, @c, @s, @b, l.value_date, l.direction, l.amount, l.reference, l.description, l.occurrence, 'UNMATCHED', 1
                FROM unnest(@ids, @dates, @directions, @amounts, @references, @descriptions, @occurrences)
                  AS l (id, value_date, direction, amount, reference, description, occurrence)
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("s", statement),
                ("b", command.BankAccountId),
                ("ids", inserted.Select(_ => context.Ids.NewId()).ToArray()),
                ("dates", inserted.Select(l => l.ValueDate).ToArray()),
                ("directions", inserted.Select(l => l.Direction).ToArray()),
                ("amounts", inserted.Select(l => l.Amount).ToArray()),
                ("references", inserted.Select(l => l.Reference).ToArray()),
                ("descriptions", inserted.Select(l => l.Description).ToArray()),
                ("occurrences", inserted.Select(l => l.Occurrence).ToArray())).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            statementId = statement,
            eventId,
            linesInFile = parsed.Lines.Count,
            inserted = inserted.Count,
            duplicates = duplicatesPayload,
        });
    }

    /// <summary>E-VS2-05-3: from the file when the format has the cell, else from the command; both present must agree.</summary>
    private static T Field<T>(string name, T? fromFile, T? fromCommand)
        where T : struct
    {
        if (fromFile is { } file)
        {
            return fromCommand is { } given && !EqualityComparer<T>.Default.Equals(given, file)
                ? throw new DomainException(StatementErrors.FieldsDisagree, $"{name}: the file says {Show(file)}, the command {Show(given)}.")
                : file;
        }

        return fromCommand ?? throw new DomainException(StatementErrors.FieldsRequired, $"The bank's format has no {name}; give it with the import (E-VS2-05-3).");
    }

    private static string Show<T>(T value)
        where T : struct
        => value switch
        {
            decimal d => PaymentRules.Money(d),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    /// <summary>
    /// E-VS2-05-5: no new line in a period whose BANK-REC is CLOSED. Each period is locked in shared mode on BANK-REC, as a posting
    /// does, so a concurrent close cannot slip in between the check and the COMMIT.
    /// </summary>
    private static async Task RequireOpenBankRecAsync(CommandContext context, IReadOnlyList<StatementLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var periods = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT period_id, starts_on, ends_on FROM fin.period WHERE company_id = @c AND ends_on >= @from AND starts_on <= @to ORDER BY starts_on",
            r => new Period(r.GetGuid(0), r.Date(1), r.Date(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("from", lines.Min(l => l.ValueDate)),
            ("to", lines.Max(l => l.ValueDate))).ConfigureAwait(false);
        foreach (var period in periods)
        {
            var rows = lines.Where(l => l.ValueDate >= period.StartsOn && l.ValueDate <= period.EndsOn).Select(l => l.Row).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "SELECT pg_advisory_xact_lock_shared(hashtextextended('period:' || @company || ':' || @period || ':' || @component, 0))",
                cancellationToken,
                ("company", context.CompanyId.ToString()),
                ("period", period.PeriodId.ToString()),
                ("component", "BANK-REC")).ConfigureAwait(false);
            var status = await PaymentRules.ScalarAsync<string>(
                context,
                "SELECT status FROM fin.close_component_state WHERE period_id = @p AND component = 'BANK-REC'",
                cancellationToken,
                ("p", period.PeriodId)).ConfigureAwait(false);
            if (status == "CLOSED")
            {
                throw new DomainException(
                    StatementErrors.PeriodClosed,
                    $"BANK-REC is closed for {period.StartsOn:yyyy-MM}; row(s) {string.Join(", ", rows)} would be new lines there. Reopen it first (E-VS2-05-5).");
            }
        }
    }
}
