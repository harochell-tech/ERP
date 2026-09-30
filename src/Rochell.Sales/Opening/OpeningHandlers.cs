using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Sales.Opening;

public static class OpeningErrors
{
    public const string FileInvalid = "OPENING_FILE_INVALID";
    public const string FileAlreadyPosted = "OPENING_FILE_ALREADY_POSTED";
    public const string DocumentAlreadyLoaded = "OPENING_DOCUMENT_ALREADY_LOADED";
    public const string CostMissing = "STANDARD_COST_MISSING";
    public const string CostChanged = "STANDARD_COST_CHANGED";
    public const string PeriodClosed = "PERIOD_CLOSED";
    public const string LotMoved = "OPENING_LOT_MOVED";
    public const string ReasonRequired = "REASON_REQUIRED";
}

internal static class Openings
{
    public const string Aggregate = "OpeningInventory";
    public const string RuleCode = "OPEN-INV";
    public const string DocumentType = "OPENING_INVENTORY";
    public const int MaxLines = 5000;

    public sealed record ParsedLine(int LineNo, string Plant, string Location, string Item, decimal Quantity, string Document);

    /// <summary>CSV: header planta,ubicacion,producto,cantidad,documento (UTF-8, optional BOM, comma separator, point decimal).</summary>
    public static List<ParsedLine> Parse(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var rows = text.Split('\n').Select(r => r.TrimEnd('\r')).Where(r => r.Trim().Length > 0).ToList();
        if (rows.Count < 2 || !string.Equals(rows[0].Replace(" ", string.Empty, StringComparison.Ordinal), "planta,ubicacion,producto,cantidad,documento", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(OpeningErrors.FileInvalid, "The file needs the header planta,ubicacion,producto,cantidad,documento and at least one line.");
        }

        if (rows.Count - 1 > MaxLines)
        {
            throw new DomainException(OpeningErrors.FileInvalid, $"A batch has at most {MaxLines} lines.");
        }

        var lines = new List<ParsedLine>();
        for (var i = 1; i < rows.Count; i++)
        {
            var f = rows[i].Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length != 5 || f.Any(x => x.Length == 0)
                || !decimal.TryParse(f[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity)
                || quantity <= 0m || decimal.Round(quantity, 6) != quantity)
            {
                throw new DomainException(OpeningErrors.FileInvalid, $"Line {i + 1}: five values and a positive quantity with at most 6 decimals.");
            }

            lines.Add(new ParsedLine(i, f[0].ToUpperInvariant(), f[1].ToUpperInvariant(), f[2].ToUpperInvariant(), quantity, f[4]));
        }

        var duplicate = lines.GroupBy(l => l.Document, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? lines : throw new DomainException(OpeningErrors.FileInvalid, $"Document {duplicate.Key} appears more than once.");
    }

    public static async Task<decimal?> ActiveCostAsync(CommandContext context, Guid itemId, Guid plantId, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<decimal?>(
            context,
            """
            SELECT s.unit_cost FROM md.standard_cost_version s JOIN md.plant p ON p.valuation_area_id = s.valuation_area_id
            WHERE s.company_id = @c AND s.item_id = @i AND p.plant_id = @p AND s.status = 'ACTIVE'
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("i", itemId),
            ("p", plantId)).ConfigureAwait(false);

    public sealed record Batch(string Status, DateOnly Cutover, Guid PreparedBy, Guid? PostingEventId, long Version);

    public static async Task<Batch> LockAsync(CommandContext context, Guid batchId, long expectedVersion, CancellationToken cancellationToken)
    {
        var batch = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, cutover_date, prepared_by, posting_event_id, version FROM mig.migration_batch WHERE company_id = @c AND batch_id = @b FOR UPDATE",
            r => new Batch(r.GetString(0), r.Date(1), r.GetGuid(2), r.NullableGuid(3), r.GetInt64(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("b", batchId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The opening batch does not exist.");
        return batch.Version == expectedVersion
            ? batch
            : throw new DomainException(SalesErrors.VersionConflict, $"The batch changed (version {batch.Version}, expected {expectedVersion}); reload and retry.");
    }

    public sealed record Line(Guid LineId, int LineNo, string Document, Guid PlantId, Guid LocationId, Guid ItemId, string ItemCode, decimal Quantity, decimal UnitCost, decimal Value, Guid? LotId);

    public static Task<List<Line>> LinesAsync(CommandContext context, Guid batchId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.line_no, l.source_document_number, l.plant_id, l.location_id, l.item_id, i.code, l.quantity, l.unit_cost, l.value, l.lot_id
            FROM mig.opening_inventory_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.batch_id = @b ORDER BY l.line_no
            """,
            r => new Line(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.NullableGuid(10)),
            cancellationToken,
            ("b", batchId));

    public static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

[RequiresPermission("opening_inventory:prepare")]
public sealed class PrepareOpeningInventoryHandler : ICommandHandler<PrepareOpeningInventory>
{
    public string CommandType => "Sales.PrepareOpeningInventory";

    public async Task<string> HandleAsync(PrepareOpeningInventory command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var fileName = (command.FileName ?? string.Empty).Trim();
        if (fileName.Length is 0 or > 200)
        {
            throw new DomainException(OpeningErrors.FileInvalid, "The file name has 1 to 200 characters.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(command.ContentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new DomainException(OpeningErrors.FileInvalid, "The file content is not valid base64.");
        }

        if (command.CutoverDate.AddDays(-1) > SalesSql.Today(context))
        {
            throw new DomainException(SalesErrors.FieldInvalid, "The opening is posted the day before the cutover, which cannot be in the future.");
        }

        var parsed = Openings.Parse(content);
        var hash = SHA256.HashData(content);
        if (await SalesSql.ScalarAsync<Guid?>(
                context, "SELECT batch_id FROM mig.migration_batch WHERE company_id = @c AND source_file_sha256 = @h AND status = 'POSTED'", cancellationToken,
                ("c", context.CompanyId), ("h", hash)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(OpeningErrors.FileAlreadyPosted, "This file is already posted in a batch (E-VS3-02b-9).");
        }

        var lines = new List<(Openings.ParsedLine Row, Guid PlantId, Guid LocationId, Guid ItemId, decimal Cost, decimal Value)>();
        foreach (var row in parsed)
        {
            var ids = await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT p.plant_id, l.location_id, i.item_id, i.item_type, i.status::text
                FROM md.plant p
                LEFT JOIN md.location l ON l.plant_id = p.plant_id AND l.code = @loc
                LEFT JOIN md.item i ON i.company_id = p.company_id AND i.code = @item
                WHERE p.company_id = @c AND p.code = @plant
                """,
                r => new[] { r.GetGuid(0).ToString(), r.IsDBNull(1) ? null : r.GetGuid(1).ToString(), r.IsDBNull(2) ? null : r.GetGuid(2).ToString(), r.NullableString(3), r.NullableString(4) },
                cancellationToken,
                ("c", context.CompanyId),
                ("plant", row.Plant),
                ("loc", row.Location),
                ("item", row.Item)).ConfigureAwait(false);
            if (ids is null || ids[1] is null)
            {
                throw new DomainException(OpeningErrors.FileInvalid, $"Line {row.LineNo + 1}: plant {row.Plant} or location {row.Location} does not exist.");
            }

            if (ids[2] is null || ids[3] != "FINISHED_GOOD" || ids[4] != "ACTIVE")
            {
                throw new DomainException(SalesErrors.NotFinishedGood, $"Line {row.LineNo + 1}: {row.Item} is not an active finished good (E-VS3-02b-3).");
            }

            var (plantId, locationId, itemId) = (Guid.Parse(ids[0]!), Guid.Parse(ids[1]!), Guid.Parse(ids[2]!));
            var cost = await Openings.ActiveCostAsync(context, itemId, plantId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException(OpeningErrors.CostMissing, $"Line {row.LineNo + 1}: {row.Item} has no approved standard cost in the valuation area of {row.Plant} (E-VS3-02b-5).");
            var value = decimal.Round(row.Quantity * cost, 2, MidpointRounding.AwayFromZero);
            if (value <= 0m)
            {
                throw new DomainException(OpeningErrors.FileInvalid, $"Line {row.LineNo + 1}: the quantity is too small to be valued.");
            }

            if (await SalesSql.ScalarAsync<Guid?>(
                    context, "SELECT line_id FROM mig.opening_inventory_line WHERE company_id = @c AND source_document_number = @d AND live", cancellationToken,
                    ("c", context.CompanyId), ("d", row.Document)).ConfigureAwait(false) is not null)
            {
                throw new DomainException(OpeningErrors.DocumentAlreadyLoaded, $"Line {row.LineNo + 1}: document {row.Document} is already in a posted batch.");
            }

            lines.Add((row, plantId, locationId, itemId, cost, value));
        }

        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var total = lines.Sum(l => l.Value);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "OpeningInventoryPrepared",
                1,
                Openings.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { batchId = context.ResultRef, fileName, sha256 = Convert.ToHexStringLower(hash), cutoverDate = command.CutoverDate, lines = lines.Count, total = Openings.Money(total) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mig.migration_batch (batch_id, company_id, kind, source_system, file_name, source_file_sha256, cutover_date, status, prepared_by, version)
            VALUES (@id, @c, 'OPENING_INVENTORY', 'ADM_CLOUD', @file, @hash, @cutover, 'DRAFT', @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("file", fileName),
            ("hash", hash),
            ("cutover", command.CutoverDate),
            ("by", preparer)).ConfigureAwait(false);
        foreach (var l in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mig.opening_inventory_line (line_id, company_id, batch_id, line_no, source_document_number, plant_id, location_id, item_id, quantity, unit_cost, value)
                VALUES (@id, @c, @b, @no, @doc, @plant, @loc, @item, @qty, @cost, @value)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("b", context.ResultRef),
                ("no", l.Row.LineNo),
                ("doc", l.Row.Document),
                ("plant", l.PlantId),
                ("loc", l.LocationId),
                ("item", l.ItemId),
                ("qty", l.Row.Quantity),
                ("cost", l.Cost),
                ("value", l.Value)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Openings.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { batchId = context.ResultRef, status = "DRAFT", lines = lines.Count, total = Openings.Money(total), version = 1 });
    }
}

[RequiresPermission("opening_inventory:post", StepUp = true)]
public sealed class PostOpeningInventoryHandler : ICommandHandler<PostOpeningInventory>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Sales.PostOpeningInventory";

    public async Task<string> HandleAsync(PostOpeningInventory command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var batch = await Openings.LockAsync(context, command.BatchId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (batch.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The batch is {batch.Status}.");
        }

        var poster = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (poster == batch.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(SalesErrors.FourEyes, "An opening batch is posted by someone other than who prepared it (E-VS3-02b-2).");
        }

        var lines = await Openings.LinesAsync(context, command.BatchId, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (await Openings.ActiveCostAsync(context, line.ItemId, line.PlantId, cancellationToken).ConfigureAwait(false) != line.UnitCost)
            {
                throw new DomainException(OpeningErrors.CostChanged, $"Line {line.LineNo + 1}: the standard cost of {line.ItemCode} changed since the batch was prepared; prepare it again.");
            }

            if (await SalesSql.ScalarAsync<Guid?>(
                    context, "SELECT line_id FROM mig.opening_inventory_line WHERE company_id = @c AND source_document_number = @d AND live", cancellationToken,
                    ("c", context.CompanyId), ("d", line.Document)).ConfigureAwait(false) is not null)
            {
                throw new DomainException(OpeningErrors.DocumentAlreadyLoaded, $"Document {line.Document} is already in a posted batch.");
            }
        }

        // E-VS3-02b-6: the day before the cutover, never moved to a later period.
        var postingDate = batch.Cutover.AddDays(-1);
        var occurredAt = context.Clock.UtcNow;
        var planned = lines.Select(l => (Line: l, ValueEntryId: context.Ids.NewId())).ToList();
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                Openings.RuleCode,
                postingDate,
                occurredAt,
                planned.SelectMany(p =>
                {
                    var inputs = new Dictionary<string, string>
                    {
                        ["source_document_number"] = p.Line.Document,
                        ["cutover_date"] = batch.Cutover.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["quantity"] = p.Line.Quantity.ToString(CultureInfo.InvariantCulture),
                        ["unit_cost"] = p.Line.UnitCost.ToString(CultureInfo.InvariantCulture),
                    };
                    return new[]
                    {
                        new PostingLineInput("OPENINV-DR-FG", "opening_value", p.Line.Value, PlantId: p.Line.PlantId, ItemId: p.Line.ItemId, SubledgerRef: p.ValueEntryId, InvValueEntryId: p.ValueEntryId, Inputs: inputs),
                        new PostingLineInput("OPENINV-CR-MIG", "opening_value", p.Line.Value, PlantId: p.Line.PlantId, Inputs: inputs),
                    };
                }).ToList()),
            cancellationToken).ConfigureAwait(false);
        if (plan.PostingDate != postingDate)
        {
            throw new DomainException(OpeningErrors.PeriodClosed, $"INV-MOV is closed for {postingDate:yyyy-MM-dd}; an opening is posted only into the period of the day before the cutover.");
        }

        var version = batch.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "OpeningInventoryPosted",
                1,
                Openings.Aggregate,
                command.BatchId,
                version,
                JsonSerializer.Serialize(new
                {
                    batchId = command.BatchId,
                    cutoverDate = batch.Cutover,
                    postingDate,
                    lines = lines.Count,
                    total = Openings.Money(lines.Sum(l => l.Value)),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: postingDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE mig.migration_batch SET status = 'POSTED', posted_by = @by, posting_event_id = @e, version = @v WHERE batch_id = @b",
            cancellationToken,
            ("by", poster),
            ("e", eventId),
            ("v", version),
            ("b", command.BatchId)).ConfigureAwait(false);

        var dates = new MovementDates(occurredAt, postingDate, plan.PostingDate);
        foreach (var (line, valueEntryId) in planned)
        {
            // E-VS3-02b-4: one lot per line, AP-<item>-<cutover>-<n>.
            var prefix = $"AP-{line.ItemCode}-{batch.Cutover:yyyyMMdd}-";
            var next = (await SalesSql.ScalarAsync<long?>(
                context, "SELECT count(*) FROM inv.lot WHERE company_id = @c AND lot_code LIKE @p || '%'", cancellationToken, ("c", context.CompanyId), ("p", prefix)).ConfigureAwait(false) ?? 0) + 1;
            var lotId = await _inventory.CreateLotAsync(context, line.ItemId, null, null, eventId, postingDate, cancellationToken, prefix + next.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await _inventory.ReceiveAsync(
                context,
                new ReceiptMovement(line.LocationId, line.ItemId, lotId, line.Quantity, line.Value, valueEntryId),
                new MovementSource(eventId, Openings.DocumentType, command.BatchId, line.LineId),
                dates,
                cancellationToken,
                MovementTypes.Opening).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE mig.opening_inventory_line SET lot_id = @lot, live = true WHERE line_id = @l", cancellationToken, ("lot", lotId), ("l", line.LineId)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Openings.Aggregate, command.BatchId, "DOCUMENT", "DRAFT", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { batchId = command.BatchId, status = "POSTED", version, journalId = journal.JournalId, postingDate });
    }
}

[RequiresPermission("opening_inventory:post", StepUp = true)]
public sealed class ReverseOpeningInventoryHandler : ICommandHandler<ReverseOpeningInventory>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Sales.ReverseOpeningInventory";

    public async Task<string> HandleAsync(ReverseOpeningInventory command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(OpeningErrors.ReasonRequired, "Reversing an opening batch needs a reason.");
        }

        var batch = await Openings.LockAsync(context, command.BatchId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (batch.Status != "POSTED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The batch is {batch.Status}.");
        }

        var lines = await Openings.LinesAsync(context, command.BatchId, cancellationToken).ConfigureAwait(false);
        var movements = new List<(Openings.Line Line, ReceiptEntries Entries, Guid ReversalValueEntryId)>();
        foreach (var line in lines)
        {
            var entries = await _inventory.ReceiptEntriesAsync(context, line.LineId, cancellationToken).ConfigureAwait(false);
            if (await _inventory.LotHasLaterMovementsAsync(context, entries.LotId, entries.QuantityEntryId, cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(OpeningErrors.LotMoved, $"Lot of document {line.Document} has moved since the opening; the batch cannot be reversed (E-VS3-02b-9).");
            }

            movements.Add((line, entries, context.Ids.NewId()));
        }

        foreach (var m in movements.OrderBy(m => m.Entries.LocationId).ThenBy(m => m.Entries.ItemId).ThenBy(m => m.Entries.LotId))
        {
            await _inventory.LockStockAsync(context, m.Entries.LocationId, m.Entries.ItemId, m.Entries.LotId, cancellationToken).ConfigureAwait(false);
        }

        var postingDate = batch.Cutover.AddDays(-1);
        var journal = await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT j.journal_id FROM fin.gl_journal j
            WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("e", batch.PostingEventId!.Value)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.InvalidState, "The batch has no live journal.");
        var plan = await _engine.PrepareReversalAsync(context, journal, postingDate, cancellationToken).ConfigureAwait(false);
        if (plan.PostingDate != postingDate)
        {
            throw new DomainException(OpeningErrors.PeriodClosed, $"INV-MOV is closed for {postingDate:yyyy-MM-dd}; the batch can no longer be reversed (E-VS3-02b-9).");
        }

        var occurredAt = context.Clock.UtcNow;
        var version = batch.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "OpeningInventoryReversed",
                1,
                Openings.Aggregate,
                command.BatchId,
                version,
                JsonSerializer.Serialize(new { batchId = command.BatchId, reason, postingDate }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: postingDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE mig.migration_batch SET status = 'REVERSED', reversal_event_id = @e, reversal_reason = @r, version = @v WHERE batch_id = @b",
            cancellationToken,
            ("e", eventId),
            ("r", reason),
            ("v", version),
            ("b", command.BatchId)).ConfigureAwait(false);

        var dates = new MovementDates(occurredAt, postingDate, plan.PostingDate);
        var valueEntryMap = new Dictionary<Guid, Guid>();
        foreach (var m in movements)
        {
            await _inventory.ReverseReceiptAsync(context, m.Entries, m.ReversalValueEntryId, new MovementSource(eventId, "OPENING_INVENTORY_REVERSAL", command.BatchId, m.Line.LineId), dates, cancellationToken).ConfigureAwait(false);
            valueEntryMap[m.Entries.ValueEntryId] = m.ReversalValueEntryId;
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mig.opening_inventory_line SET live = false WHERE line_id = @l", cancellationToken, ("l", m.Line.LineId)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Openings.Aggregate, command.BatchId, "DOCUMENT", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, occurredAt, cancellationToken, valueEntryMap).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { batchId = command.BatchId, status = "REVERSED", version, reversalJournalId = reversal.JournalId });
    }
}
