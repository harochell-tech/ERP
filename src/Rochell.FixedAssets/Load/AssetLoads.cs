using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.FixedAssets.Cards;
using Rochell.MasterData.Import;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.FixedAssets.Load;

/// <summary>
/// E-AF-8, E-AF1-04-1…5: what loading the existing assets of a CSV / Excel file at <paramref name="CutoffDate"/> (a month's last day) would
/// do — each row with its months depreciated, months left and monthly amount, or why it is refused. Nothing is written.
/// </summary>
public sealed record PreviewAssetLoad(Guid CompanyId, Guid SessionId, string FileName, string ContentBase64, DateOnly CutoffDate) : IQuery;

/// <summary>E-AF-8: the Contador prepares the load (DRAFT) — refused whole when one row has an error (E-AF1-04-4).</summary>
public sealed record PrepareAssetLoad(Guid CompanyId, Guid SessionId, string IdempotencyKey, string FileName, string ContentBase64, DateOnly CutoffDate) : ICommand;

/// <summary>Discards a DRAFT load.</summary>
public sealed record DiscardAssetLoad(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LoadId, long ExpectedVersion) : ICommand;

/// <summary>
/// E-AF-8: the Controller (not the preparer, step-up) approves the load: a card in service per row and P-46 on the cut-off date — cost to the
/// category's account, accumulated to the class's, the book value against «Contrapartida de saldos de apertura».
/// </summary>
public sealed record ApproveAssetLoad(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LoadId, long ExpectedVersion) : ICommand;

/// <summary>E-AF1-04-6: reverses a POSTED load while none of its cards was depreciated or disposed of since; its cards are cancelled.</summary>
public sealed record ReverseAssetLoad(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LoadId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>The body of the load preview (the file travels in the body; read-only).</summary>
public sealed record AssetLoadPreviewRequest(string FileName, string ContentBase64, DateOnly CutoffDate);

public static class AssetLoadErrors
{
    public const string FileInvalid = "ASSET_LOAD_FILE_INVALID";
    public const string RowsInvalid = "ASSET_LOAD_ROWS_INVALID";
    public const string CutoffInvalid = "ASSET_LOAD_CUTOFF_INVALID";
    public const string NotFound = "ASSET_LOAD_NOT_FOUND";
}

public sealed record AssetLoadRow(
    int Row, string Code, string Description, string CategoryCode, string PlantCode, DateOnly? AcquiredOn, decimal? Cost, decimal? Accumulated, int? MonthsDepreciated,
    int? UsefulLifeMonths, decimal? ResidualValue, decimal? MonthlyAmount, string? Error);

public sealed record AssetLoadPreview(
    string FileName, DateOnly CutoffDate, int Rows, int Valid, int Invalid, decimal TotalCost, decimal TotalAccumulated, decimal TotalBookValue, IReadOnlyList<AssetLoadRow> Items);

internal static class AssetLoadBook
{
    public const string Aggregate = "AssetLoad";
    private const int MaxFileBytes = 5 * 1024 * 1024; // type-limit: the import panel's limit (E-IMP-01-1)
    private const int ExcelEpochYear = 1899;

    /// <summary>A valid row with what the card needs.</summary>
    public sealed record ValidRow(
        int Row, string Code, string Description, Guid CategoryId, Guid CostAccountId, Guid PlantId, DateOnly AcquiredOn, decimal Cost, decimal Accumulated, int Months,
        Guid ClassId, int Life, decimal ResidualPct, Guid AccumulatedAccountId);

    public static void RequireCutoff(DateOnly cutoff, DateOnly today)
    {
        if (cutoff != new DateOnly(cutoff.Year, cutoff.Month, 1).AddMonths(1).AddDays(-1) || cutoff > today)
        {
            throw new DomainException(AssetLoadErrors.CutoffInvalid, "The cut-off is the last day of a month that has ended (E-AF1-04-2).");
        }
    }

    /// <summary>E-AF1-04-1: the file's rows as text: Código, Descripción, Categoría, Planta, Fecha de compra, Costo, Depreciación acumulada.</summary>
    public static IReadOnlyList<(int Row, string[] Cells)> Read(string? fileName, string? contentBase64)
    {
        if ((fileName ?? string.Empty).Trim().Length is 0 or > 200)
        {
            throw new DomainException(AssetLoadErrors.FileInvalid, "The file name has 1 to 200 characters.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(contentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new DomainException(AssetLoadErrors.FileInvalid, "The file content is not valid base64.");
        }

        if (content.Length is 0 or > MaxFileBytes)
        {
            throw new DomainException(AssetLoadErrors.FileInvalid, "The file is empty or larger than 5 MB.");
        }

        IReadOnlyList<IReadOnlyList<string>> sheet;
        try
        {
            sheet = SpreadsheetFile.Read(content);
        }
        catch (SpreadsheetException ex)
        {
            throw new DomainException(AssetLoadErrors.FileInvalid, ex.Message);
        }

        if (sheet.Count < 2)
        {
            throw new DomainException(AssetLoadErrors.FileInvalid, "The file needs a header row and at least one asset.");
        }

        string[] names = ["codigo", "descripcion", "categoria", "planta", "fecha de compra", "costo", "depreciacion acumulada"];
        var header = sheet[0].Select(Key).ToList();
        var columns = names.Select(n => header.IndexOf(n)).ToArray();
        if (columns.Any(c => c < 0))
        {
            throw new DomainException(
                AssetLoadErrors.FileInvalid,
                "The first row has the columns Código, Descripción, Categoría, Planta, Fecha de compra, Costo and Depreciación acumulada (E-AF1-04-1).");
        }

        var rows = new List<(int, string[])>();
        for (var i = 1; i < sheet.Count; i++)
        {
            var cells = sheet[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add((i + 1, [.. columns.Select(c => c < cells.Count ? cells[c].Trim() : string.Empty)]));
        }

        return rows;
    }

    /// <summary>E-AF1-04-3/4: each row judged against the categories, classes, plants, live cards and the other rows.</summary>
    public static async Task<(IReadOnlyList<AssetLoadRow> Rows, IReadOnlyList<ValidRow> Valid)> AnalyzeAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid companyId, IReadOnlyList<(int Row, string[] Cells)> rows,
        DateOnly cutoff, CancellationToken cancellationToken)
    {
        var categories = (await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT c.code, c.expense_category_id, c.account_id, k.asset_class_id, k.useful_life_months, k.residual_pct, k.accumulated_account_id
            FROM pur.expense_category c JOIN fin.account a ON a.account_id = c.account_id
            LEFT JOIN fa.asset_class k ON k.expense_category_id = c.expense_category_id AND k.status = 'ACTIVE'
            WHERE c.company_id = @c AND c.goods_type_606 = '04' AND a.account_class = 'ASSET'
            """,
            r => (Code: r.GetString(0), Id: r.GetGuid(1), Account: r.GetGuid(2), ClassId: r.NullableGuid(3), Life: r.IsDBNull(4) ? 0 : r.GetInt32(4),
                Residual: r.NullableDecimal(5) ?? 0m, Accumulated: r.NullableGuid(6)),
            cancellationToken,
            ("c", companyId)).ConfigureAwait(false)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var plants = (await Reading.ListAsync(
            connection, transaction, "SELECT code, plant_id FROM md.plant WHERE company_id = @c", r => (Code: r.GetString(0), Id: r.GetGuid(1)), cancellationToken, ("c", companyId))
            .ConfigureAwait(false)).ToDictionary(p => p.Code, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var used = (await Reading.ListAsync(
            connection, transaction, "SELECT external_code FROM fa.asset WHERE company_id = @c AND external_code IS NOT NULL AND status <> 'CANCELLED'", r => r.GetString(0),
            cancellationToken, ("c", companyId)).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new List<AssetLoadRow>(rows.Count);
        var valid = new List<ValidRow>(rows.Count);
        foreach (var (row, cells) in rows)
        {
            var (code, description, categoryCode, plantCode) = (cells[0], cells[1], cells[2], cells[3]);
            var acquired = ParseDate(cells[4]);
            var cost = ParseAmount(cells[5]);
            var accumulated = ParseAmount(cells[6]);
            string? error = null;
            int? months = null;
            int? life = null;
            decimal? residual = null;
            decimal? monthly = null;
            categories.TryGetValue(categoryCode, out var category);
            if (code.Length is 0 or > 40 || description.Length is 0 or > 200)
            {
                error = "El código tiene de 1 a 40 caracteres y la descripción de 1 a 200.";
            }
            else if (!seen.Add(code) || used.Contains(code))
            {
                error = $"El código {code} ya está en otra fila o en un activo existente.";
            }
            else if (category == default)
            {
                error = $"La categoría {categoryCode} no existe o no es de activos fijos.";
            }
            else if (category.ClassId is null)
            {
                error = $"La categoría {categoryCode} no tiene una clase aprobada (vida útil, residual y cuentas).";
            }
            else if (!plants.ContainsKey(plantCode))
            {
                error = $"La planta {plantCode} no existe.";
            }
            else if (acquired is null || acquired > cutoff)
            {
                error = "La fecha de compra falta, no se entiende (use AAAA-MM-DD o DD/MM/AAAA) o es posterior al corte.";
            }
            else if (cost is not > 0 || accumulated is not >= 0 || cost != decimal.Round(cost.Value, 2) || accumulated != decimal.Round(accumulated.Value, 2))
            {
                error = "El costo es mayor que cero y la depreciación acumulada cero o más, con hasta 2 decimales.";
            }
            else
            {
                life = category.Life;
                residual = FixedAssetCards.Residual(cost.Value, category.Residual);
                var elapsed = ((cutoff.Year * 12) + cutoff.Month) - ((acquired.Value.Year * 12) + acquired.Value.Month);
                months = Math.Min(Math.Max(elapsed, 0), life.Value);
                var remaining = cost.Value - residual.Value - accumulated.Value;
                if (remaining < 0)
                {
                    error = $"La depreciación acumulada supera el costo menos el residual ({FixedAssetCards.Text(cost.Value - residual.Value)}).";
                }
                else if (months >= life && remaining > 0)
                {
                    error = $"Su vida útil de {life} meses terminó al corte y aún le quedan {FixedAssetCards.Text(remaining)} por depreciar.";
                }
                else
                {
                    monthly = remaining == 0 ? 0m : life - months == 1 ? remaining : decimal.Round(remaining / (life.Value - months.Value), 2, MidpointRounding.AwayFromZero);
                    valid.Add(new ValidRow(
                        row, code, description, category.Id, category.Account, plants[plantCode], acquired.Value, cost.Value, accumulated.Value, months.Value,
                        category.ClassId.Value, life.Value, category.Residual, category.Accumulated!.Value));
                }
            }

            result.Add(new AssetLoadRow(row, code, description, categoryCode, plantCode, acquired, cost, accumulated, months, life, residual, monthly, error));
        }

        return (result, valid);
    }

    public static AssetLoadPreview Preview(string fileName, DateOnly cutoff, IReadOnlyList<AssetLoadRow> rows, IReadOnlyList<ValidRow> valid)
        => new(
            fileName.Trim(),
            cutoff,
            rows.Count,
            valid.Count,
            rows.Count - valid.Count,
            valid.Sum(v => v.Cost),
            valid.Sum(v => v.Accumulated),
            valid.Sum(v => v.Cost - v.Accumulated),
            rows);

    public static DateOnly? ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        // An Excel date serial, as the workbook stores it (days since 1899-12-30).
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 100000
            ? new DateOnly(ExcelEpochYear, 12, 30).AddDays(serial)
            : null;
    }

    public static decimal? ParseAmount(string text)
    {
        var cleaned = text.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string Key(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            builder.Append(c switch
            {
                'á' => 'a',
                'é' => 'e',
                'í' => 'i',
                'ó' => 'o',
                'ú' or 'ü' => 'u',
                'ñ' => 'n',
                _ => c,
            });
        }

        return builder.ToString();
    }

    public sealed record Header(Guid Id, DateOnly Cutoff, string FileName, string Status, Guid PreparedBy, Guid? PostingEventId, long Version);

    public static async Task<Header> LockAsync(CommandContext context, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var header = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT load_id, cutoff_date, file_name, status, prepared_by, posting_event_id, version FROM fa.asset_load WHERE company_id = @c AND load_id = @l FOR UPDATE",
            r => new Header(r.GetGuid(0), r.Date(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.NullableGuid(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", id)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(AssetLoadErrors.NotFound, "The load does not exist.");
        return header.Version == expectedVersion
            ? header
            : throw new DomainException(FixedAssetErrors.VersionConflict, $"The load changed (version {header.Version}, expected {expectedVersion}); reload and retry.");
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class PreviewAssetLoadHandler : IQueryHandler<PreviewAssetLoad>
{
    public string QueryType => "FixedAssets.PreviewAssetLoad";

    public async Task<string> HandleAsync(PreviewAssetLoad query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        AssetLoadBook.RequireCutoff(query.CutoffDate, Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow));
        var rows = AssetLoadBook.Read(query.FileName, query.ContentBase64);
        var (analyzed, valid) = await AssetLoadBook.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, rows, query.CutoffDate, cancellationToken)
            .ConfigureAwait(false);
        return ApiJson.Serialize(AssetLoadBook.Preview(query.FileName, query.CutoffDate, analyzed, valid));
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class PrepareAssetLoadHandler : ICommandHandler<PrepareAssetLoad>
{
    public string CommandType => "FixedAssets.PrepareAssetLoad";

    public async Task<string> HandleAsync(PrepareAssetLoad command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        AssetLoadBook.RequireCutoff(command.CutoffDate, FixedAssetCards.BusinessDate(context));
        var rows = AssetLoadBook.Read(command.FileName, command.ContentBase64);
        var (analyzed, valid) = await AssetLoadBook.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, rows, command.CutoffDate, cancellationToken)
            .ConfigureAwait(false);
        var invalid = analyzed.Where(r => r.Error is not null).ToList();
        if (invalid.Count > 0)
        {
            throw new DomainException(
                AssetLoadErrors.RowsInvalid,
                $"{invalid.Count} row(s) have errors; nothing is loaded (E-AF1-04-4). First: row {invalid[0].Row}: {invalid[0].Error}");
        }

        var preparer = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var fileName = command.FileName.Trim();
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AssetLoadPrepared", 1, AssetLoadBook.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { loadId = context.ResultRef, fileName, cutoffDate = command.CutoffDate, rows = valid.Count, cost = FixedAssetCards.Text(valid.Sum(v => v.Cost)) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO fa.asset_load (load_id, company_id, cutoff_date, file_name, status, prepared_by, version) VALUES (@id, @c, @d, @f, 'DRAFT', @by, 1)",
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("d", command.CutoffDate),
            ("f", fileName),
            ("by", preparer)).ConfigureAwait(false);
        var lineNo = 0;
        foreach (var v in valid)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fa.asset_load_line (load_id, company_id, line_no, external_code, description, expense_category_id, plant_id, acquired_on, cost, accumulated)
                VALUES (@l, @c, @n, @code, @d, @cat, @p, @on, @cost, @acc)
                """,
                cancellationToken,
                ("l", context.ResultRef),
                ("c", context.CompanyId),
                ("n", ++lineNo),
                ("code", v.Code),
                ("d", v.Description),
                ("cat", v.CategoryId),
                ("p", v.PlantId),
                ("on", v.AcquiredOn),
                ("cost", v.Cost),
                ("acc", v.Accumulated)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(AssetLoadBook.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { loadId = context.ResultRef, rows = valid.Count, status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class DiscardAssetLoadHandler : ICommandHandler<DiscardAssetLoad>
{
    public string CommandType => "FixedAssets.DiscardAssetLoad";

    public async Task<string> HandleAsync(DiscardAssetLoad command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await AssetLoadBook.LockAsync(context, command.LoadId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status != "DRAFT")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The load is {header.Status}: only a draft is discarded.");
        }

        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("AssetLoadDiscarded", 1, AssetLoadBook.Aggregate, header.Id, version, JsonSerializer.Serialize(new { loadId = header.Id }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fa.asset_load SET status = 'DISCARDED', version = @v WHERE load_id = @l", cancellationToken,
            ("v", version), ("l", header.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(AssetLoadBook.Aggregate, header.Id, "DOCUMENT", "DRAFT", "DISCARDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { loadId = header.Id, status = "DISCARDED", version });
    }
}

[RequiresPermission("fixed_asset:approve", StepUp = true)]
public sealed class ApproveAssetLoadHandler : ICommandHandler<ApproveAssetLoad>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "FixedAssets.ApproveAssetLoad";

    public async Task<string> HandleAsync(ApproveAssetLoad command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await AssetLoadBook.LockAsync(context, command.LoadId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status != "DRAFT")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The load is {header.Status}: only a draft is approved.");
        }

        var approver = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == header.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FixedAssetErrors.ApproverIsCreator, "Who prepared a load does not approve it (E-AF-8).");
        }

        // The rows are judged again: a class may have changed, or a code been used, since the draft.
        var stored = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.external_code, l.description, c.code, p.code, to_char(l.acquired_on, 'YYYY-MM-DD'), l.cost::numeric(19,2)::text, l.accumulated::numeric(19,2)::text
            FROM fa.asset_load_line l JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id JOIN md.plant p ON p.plant_id = l.plant_id
            WHERE l.load_id = @l ORDER BY l.line_no
            """,
            r => (r.GetInt32(0), new[] { r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7) }),
            cancellationToken,
            ("l", header.Id)).ConfigureAwait(false);
        var (analyzed, valid) = await AssetLoadBook.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, stored, header.Cutoff, cancellationToken).ConfigureAwait(false);
        var invalid = analyzed.Where(r => r.Error is not null).ToList();
        if (invalid.Count > 0)
        {
            throw new DomainException(AssetLoadErrors.RowsInvalid, $"{invalid.Count} row(s) are no longer valid. First: line {invalid[0].Row}: {invalid[0].Error}");
        }

        var now = context.Clock.UtcNow;
        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AssetLoadPosted", 1, AssetLoadBook.Aggregate, header.Id, version,
                JsonSerializer.Serialize(new
                {
                    loadId = header.Id,
                    cutoffDate = header.Cutoff,
                    rows = valid.Select(v => new { code = v.Code, cost = FixedAssetCards.Text(v.Cost), accumulated = FixedAssetCards.Text(v.Accumulated), months = v.Months }),
                    approvedBy = approver,
                }),
                Publish: true,
                OccurredAt: now,
                BusinessDate: header.Cutoff),
            cancellationToken).ConfigureAwait(false);

        // Each card is numbered and written in turn (AF-YYYY of the cut-off, E-AF1-04-5), then the journal of all of them.
        var inputs = new List<PostingLineInput>();
        foreach (var v in valid)
        {
            var id = context.Ids.NewId();
            var number = await FixedAssetCards.NextNumberAsync(context, header.Cutoff.Year, cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fa.asset (asset_id, company_id, asset_no, expense_category_id, description, quantity, source_kind, load_id, external_code, plant_id, acquired_on, status,
                                      in_service_on, asset_class_id, useful_life_months, residual_pct, months_depreciated, cost, accumulated, created_by, version)
                VALUES (@id, @c, @no, @cat, @d, 1, 'OPENING', @l, @code, @p, @on, 'IN_SERVICE', @on, @k, @life, @pct, @m, @cost, @acc, @by, 1)
                """,
                cancellationToken,
                ("id", id),
                ("c", context.CompanyId),
                ("no", number),
                ("cat", v.CategoryId),
                ("d", v.Description),
                ("l", header.Id),
                ("code", v.Code),
                ("p", v.PlantId),
                ("on", v.AcquiredOn),
                ("k", v.ClassId),
                ("life", v.Life),
                ("pct", v.ResidualPct),
                ("m", v.Months),
                ("cost", v.Cost),
                ("acc", v.Accumulated),
                ("by", header.PreparedBy)).ConfigureAwait(false);
            await FixedAssetCards.MovementAsync(context, id, "OPENING", header.Cutoff, v.Cost, v.PlantId, header.Id, eventId, cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(FixedAssetCards.Aggregate, id, "DOCUMENT", null, FixedAssetCards.InService, CommandType, eventId, cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, string>
            {
                ["asset"] = number,
                ["description"] = v.Description,
                ["cutoff"] = header.Cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["external_code"] = v.Code,
            };
            inputs.Add(new PostingLineInput("P46-DR-COST", "cost", v.Cost, PlantId: v.PlantId, AccountId: v.CostAccountId, Inputs: values));
            inputs.Add(new PostingLineInput("P46-CR-ACC", "accumulated", v.Accumulated, PlantId: v.PlantId, AccountId: v.AccumulatedAccountId, Inputs: values));
            inputs.Add(new PostingLineInput("P46-CR-OPEN", "book_value", v.Cost - v.Accumulated, PlantId: v.PlantId, Inputs: values));
        }

        var plan = await _engine.PrepareAsync(context, new PostingRequest("P-46", header.Cutoff, now, inputs), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fa.asset_load SET status = 'POSTED', approved_by = @by, approved_at = @at, posting_event_id = @e, version = @v WHERE load_id = @l",
            cancellationToken,
            ("by", approver),
            ("at", now),
            ("e", eventId),
            ("v", version),
            ("l", header.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(AssetLoadBook.Aggregate, header.Id, "DOCUMENT", "DRAFT", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            loadId = header.Id,
            status = "POSTED",
            cards = valid.Count,
            cost = FixedAssetCards.Text(valid.Sum(v => v.Cost)),
            accumulated = FixedAssetCards.Text(valid.Sum(v => v.Accumulated)),
            journals = new[] { journal.JournalId },
            version,
        });
    }
}

[RequiresPermission("fixed_asset:approve", StepUp = true)]
public sealed class ReverseAssetLoadHandler : ICommandHandler<ReverseAssetLoad>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "FixedAssets.ReverseAssetLoad";

    public async Task<string> HandleAsync(ReverseAssetLoad command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length < 10)
        {
            throw new DomainException(FixedAssetErrors.InvalidState, "Reversing a load needs a reason of at least 10 characters.");
        }

        var header = await AssetLoadBook.LockAsync(context, command.LoadId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status != "POSTED")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The load is {header.Status}: only a posted load is reversed.");
        }

        var cards = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"SELECT {FixedAssetCards.CardColumns}, EXISTS (SELECT 1 FROM fa.asset_movement m WHERE m.asset_id = x.asset_id AND m.kind = 'DEPRECIATION') "
            + "FROM fa.asset x WHERE x.load_id = @l ORDER BY x.asset_no FOR UPDATE OF x",
            r => (Card: FixedAssetCards.ReadCard(r), Depreciated: r.GetBoolean(12)),
            cancellationToken,
            ("l", header.Id)).ConfigureAwait(false);
        var blocked = cards.Where(c => c.Depreciated || c.Card.Status != FixedAssetCards.InService).Select(c => c.Card.Number).ToList();
        if (blocked.Count > 0)
        {
            throw new DomainException(
                FixedAssetErrors.AssetDepreciated, $"{string.Join(", ", blocked)} were depreciated or disposed of since the load; it is no longer reversed (E-AF1-04-6).");
        }

        var journalId = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT journal_id FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'AUTO'",
            r => r.GetGuid(0), cancellationToken, ("c", context.CompanyId), ("e", header.PostingEventId!.Value)).ConfigureAwait(false)).Single();
        var now = context.Clock.UtcNow;
        var businessDate = FixedAssetCards.BusinessDate(context);
        var plan = await _engine.PrepareReversalAsync(context, journalId, businessDate, cancellationToken).ConfigureAwait(false);
        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("AssetLoadReversed", 1, AssetLoadBook.Aggregate, header.Id, version, JsonSerializer.Serialize(new { loadId = header.Id, reversedJournalId = journalId, reason }),
                Publish: true, OccurredAt: now, BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        foreach (var (card, _) in cards)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE fa.asset SET status = 'CANCELLED', version = version + 1 WHERE asset_id = @a", cancellationToken, ("a", card.Id))
                .ConfigureAwait(false);
            await FixedAssetCards.MovementAsync(context, card.Id, "CANCELLED", businessDate, null, null, header.Id, eventId, cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(FixedAssetCards.Aggregate, card.Id, "DOCUMENT", card.Status, FixedAssetCards.Cancelled, CommandType, eventId, cancellationToken, reason)
                .ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fa.asset_load SET status = 'REVERSED', version = @v WHERE load_id = @l", cancellationToken,
            ("v", version), ("l", header.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(AssetLoadBook.Aggregate, header.Id, "DOCUMENT", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { loadId = header.Id, status = "REVERSED", journals = new[] { reversal.JournalId }, version });
    }
}

/// <summary>E-AF-8: the loads, newest first.</summary>
public sealed record ListAssetLoads(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record AssetLoadView(
    Guid LoadId, DateOnly CutoffDate, string FileName, string Status, int Rows, decimal Cost, decimal Accumulated, string? PreparedBy, string? ApprovedBy, long Version);

public sealed record AssetLoadList(IReadOnlyList<AssetLoadView> Items);

[RequiresPermission("ledger:read")]
public sealed class ListAssetLoadsHandler : IQueryHandler<ListAssetLoads>
{
    public string QueryType => "FixedAssets.ListAssetLoads";

    public async Task<string> HandleAsync(ListAssetLoads query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.load_id, l.cutoff_date, l.file_name, l.status, count(x.line_no)::int, coalesce(sum(x.cost), 0)::numeric(19,2), coalesce(sum(x.accumulated), 0)::numeric(19,2),
                   coalesce(p.display_name, p.email), coalesce(a.display_name, a.email), l.version
            FROM fa.asset_load l
            LEFT JOIN fa.asset_load_line x ON x.load_id = l.load_id
            LEFT JOIN iam.user p ON p.user_id = l.prepared_by
            LEFT JOIN iam.user a ON a.user_id = l.approved_by
            WHERE l.company_id = @c
            GROUP BY l.load_id, p.display_name, p.email, a.display_name, a.email
            ORDER BY l.cutoff_date DESC, l.load_id DESC
            """,
            r => new AssetLoadView(r.GetGuid(0), r.Date(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.NullableString(7), r.NullableString(8),
                r.GetInt64(9)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new AssetLoadList(items));
    }
}
