using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.Imports;

/// <summary>
/// A document of a shipment (E-USD1-04-3): <c>SUPPLIER_INVOICE</c> — a foreign invoice in USD whose lines receive the cost;
/// <c>EXPENSE_INVOICE</c> — an expense invoice (pesos or USD: freight, insurance, customs agent, local transport) that brings its net;
/// <c>CUSTOMS_DECLARATION</c> — a DUA that brings its duties and other charges.
/// </summary>
public sealed record ImportSettlementDocumentInput(string Kind, Guid DocumentId);

/// <summary>E-USD1-04-6: Cuentas por pagar prepares a DRAFT settlement (LI-YYYY-NNNNNN) of one plant with its documents; the allocation is computed.</summary>
public sealed record PrepareImportSettlement(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, DateOnly SettlementDate, string? Reference, IReadOnlyList<ImportSettlementDocumentInput> Documents) : ICommand;

/// <summary>E-USD1-04-6: replaces the date, reference and documents of a DRAFT settlement; the allocation is computed again.</summary>
public sealed record UpdateImportSettlementDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SettlementId, long ExpectedVersion, DateOnly SettlementDate, string? Reference,
    IReadOnlyList<ImportSettlementDocumentInput> Documents) : ICommand;

/// <summary>E-USD1-04-6: a DRAFT settlement is cancelled; its documents are free again.</summary>
public sealed record CancelImportSettlement(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SettlementId, long ExpectedVersion) : ICommand;

/// <summary>E-USD1-04-5/6: the Controller (not who prepared it) approves the settlement and it is posted with P-40 on its date.</summary>
public sealed record ApproveImportSettlement(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SettlementId, long ExpectedVersion) : ICommand;

/// <summary>E-USD1-04-7: the exact reversal of a POSTED settlement; its documents are free for another one.</summary>
public sealed record ReverseImportSettlement(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SettlementId, long ExpectedVersion, string Reason) : ICommand;

internal static class ImportSettlements
{
    public const string Aggregate = "ImportSettlement";
    public const string P40 = "P-40";
    public const string GoodsKind = "SUPPLIER_INVOICE";
    public const string ExpenseKind = "EXPENSE_INVOICE";
    public const string DuaKind = "CUSTOMS_DECLARATION";
    public const string Prefix = DocumentNumbers.ImportSettlement;

    /// <summary>A line of the shipment (receives cost) or of an expense invoice (brings its net): its account, category and description.</summary>
    public sealed record SourceLine(Guid DocumentId, Guid LineId, int LineNo, decimal Net, Guid AccountId, string Category, string Description, Guid PartyId, string Number);

    public sealed record Gathered(
        IReadOnlyList<(string Kind, Guid Id, decimal Cost)> Documents, IReadOnlyList<SourceLine> Goods, IReadOnlyList<SourceLine> Expenses,
        IReadOnlyList<(Guid Id, string Number, Guid PartyId, decimal Cost)> Duas, IReadOnlyList<(Guid LineId, decimal Base, decimal Added)> Allocation, DateOnly LatestDate);

    /// <summary>E-USD1-04-7: refuses when the document is in a DRAFT or POSTED settlement.</summary>
    public static async Task RequireNotSettledAsync(CommandContext context, string kind, Guid documentId, CancellationToken cancellationToken)
    {
        var settlement = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.settlement_no FROM pur.import_settlement_document d JOIN pur.import_settlement s ON s.settlement_id = d.settlement_id
            WHERE d.company_id = @c AND d.document_kind = @k AND d.document_id = @id AND s.status IN ('DRAFT', 'POSTED')
            """,
            r => r.GetString(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("k", kind),
            ("id", documentId)).ConfigureAwait(false)).FirstOrDefault();
        if (settlement is not null)
        {
            throw new DomainException(ImportErrors.InSettlement, $"The document is in settlement {settlement}; cancel or reverse it first (E-USD1-04-7).");
        }
    }

    /// <summary>
    /// E-USD1-04-3/4: locks and checks the documents — POSTED, of the plant, in no other live settlement — and spreads their cost over the
    /// goods lines by peso value, the cent left to the largest line.
    /// </summary>
    public static async Task<Gathered> GatherAsync(
        CommandContext context, Guid plantId, Guid? settlementId, IReadOnlyList<ImportSettlementDocumentInput> documents, CancellationToken cancellationToken)
    {
        if (documents is not { Count: > 0 and <= 100 })
        {
            throw new DomainException(ImportErrors.SettlementInvalid, "A settlement gathers 1 to 100 documents.");
        }

        var inputs = documents.Select(d => (Kind: (d.Kind ?? string.Empty).Trim().ToUpperInvariant(), Id: d.DocumentId)).ToList();
        if (inputs.Any(d => d.Kind is not (GoodsKind or ExpenseKind or DuaKind)))
        {
            throw new DomainException(ImportErrors.SettlementDocumentInvalid, "A document is SUPPLIER_INVOICE, EXPENSE_INVOICE or CUSTOMS_DECLARATION.");
        }

        if (inputs.Select(d => d.Id).Distinct().Count() != inputs.Count)
        {
            throw new DomainException(ImportErrors.SettlementDocumentInvalid, "Each document appears once.");
        }

        var invoiceIds = inputs.Where(d => d.Kind != DuaKind).Select(d => d.Id).Order().ToArray();
        var duaIds = inputs.Where(d => d.Kind == DuaKind).Select(d => d.Id).Order().ToArray();
        var invoices = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT si_id, currency, doc_class, accounting_status::text, plant_id, doc_date, supplier_fiscal_number, party_id
            FROM pur.supplier_invoice WHERE company_id = @c AND si_id = ANY(@ids) ORDER BY si_id FOR UPDATE
            """,
            r => (Id: r.GetGuid(0), Currency: r.GetString(1).Trim(), Class: r.GetString(2), Status: r.GetString(3), Plant: r.IsDBNull(4) ? Guid.Empty : r.GetGuid(4), Date: r.Date(5),
                Number: r.GetString(6), Party: r.GetGuid(7)),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", invoiceIds)).ConfigureAwait(false)).ToDictionary(i => i.Id);
        var duas = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT dua_id, status, plant_id, dua_date, dua_no, party_id, duties_amount + other_amount FROM pur.customs_declaration WHERE company_id = @c AND dua_id = ANY(@ids) ORDER BY dua_id FOR UPDATE",
            r => (Id: r.GetGuid(0), Status: r.GetString(1), Plant: r.GetGuid(2), Date: r.Date(3), Number: r.GetString(4), Party: r.GetGuid(5), Cost: r.GetDecimal(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", duaIds)).ConfigureAwait(false)).ToDictionary(d => d.Id);

        foreach (var (kind, id) in inputs)
        {
            if (kind == DuaKind)
            {
                if (!duas.TryGetValue(id, out var dua) || dua.Status != "POSTED" || dua.Plant != plantId)
                {
                    throw new DomainException(ImportErrors.SettlementDocumentInvalid, $"DUA {id} does not exist, is not POSTED or is of another plant.");
                }
            }
            else if (!invoices.TryGetValue(id, out var invoice) || invoice.Status != "POSTED" || invoice.Class != "EXPENSE" || invoice.Plant != plantId
                     || (kind == GoodsKind && invoice.Currency != "USD"))
            {
                throw new DomainException(
                    ImportErrors.SettlementDocumentInvalid,
                    $"Invoice {id} does not exist, is not a POSTED expense invoice of the plant{(kind == GoodsKind ? " or is not a foreign invoice in USD" : string.Empty)}.");
            }

            var other = (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT s.settlement_no FROM pur.import_settlement_document d JOIN pur.import_settlement s ON s.settlement_id = d.settlement_id
                WHERE d.company_id = @c AND d.document_id = @id AND s.status IN ('DRAFT', 'POSTED') AND (CAST(@self AS uuid) IS NULL OR s.settlement_id <> CAST(@self AS uuid))
                """,
                r => r.GetString(0),
                cancellationToken,
                ("c", context.CompanyId),
                ("id", id),
                ("self", settlementId)).ConfigureAwait(false)).FirstOrDefault();
            if (other is not null)
            {
                throw new DomainException(ImportErrors.InSettlement, $"Document {id} is already in settlement {other} (E-USD1-04-7).");
            }
        }

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.si_id, l.si_line_id, l.line_no, l.net_amount, c.account_id, c.code, l.description
            FROM pur.supplier_invoice_line l JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
            WHERE l.si_id = ANY(@ids) ORDER BY l.si_id, l.line_no
            """,
            r => (Invoice: r.GetGuid(0), Line: r.GetGuid(1), No: r.GetInt32(2), Net: r.GetDecimal(3), Account: r.GetGuid(4), Category: r.GetString(5), Description: r.GetString(6)),
            cancellationToken,
            ("ids", invoiceIds)).ConfigureAwait(false);
        SourceLine Source((Guid Invoice, Guid Line, int No, decimal Net, Guid Account, string Category, string Description) l)
            => new(l.Invoice, l.Line, l.No, l.Net, l.Account, l.Category, l.Description, invoices[l.Invoice].Party, invoices[l.Invoice].Number);
        var goodsIds = inputs.Where(d => d.Kind == GoodsKind).Select(d => d.Id).ToHashSet();
        var goods = lines.Where(l => goodsIds.Contains(l.Invoice)).Select(Source).ToList();
        var expenses = lines.Where(l => !goodsIds.Contains(l.Invoice)).Select(Source).ToList();
        var duaCosts = duaIds.Select(id => (id, duas[id].Number, duas[id].Party, duas[id].Cost)).ToList();
        var cost = expenses.Sum(l => l.Net) + duaCosts.Sum(d => d.Cost);
        if (goods.Count == 0 || cost <= 0m)
        {
            throw new DomainException(ImportErrors.SettlementInvalid, "A settlement has at least one foreign invoice of goods and a cost to spread (E-USD1-04-3).");
        }

        // E-USD1-04-4: by peso value; the cent left goes to the largest line (the first of the largest).
        var total = goods.Sum(l => l.Net);
        var added = goods.Select(l => decimal.Round(cost * l.Net / total, 2, MidpointRounding.AwayFromZero)).ToList();
        var largest = goods.Select((l, i) => (l.Net, i)).OrderByDescending(t => t.Net).ThenBy(t => t.i).First().i;
        added[largest] += cost - added.Sum();

        var documentCosts = inputs.Select(d => (d.Kind, d.Id, d.Kind switch
        {
            DuaKind => duas[d.Id].Cost,
            ExpenseKind => expenses.Where(l => l.DocumentId == d.Id).Sum(l => l.Net),
            _ => 0m,
        })).ToList();
        var latest = invoices.Values.Select(i => i.Date).Concat(duas.Values.Select(d => d.Date)).Max();
        return new Gathered(documentCosts, goods, expenses, duaCosts, [.. goods.Select((l, i) => (l.LineId, l.Net, added[i]))], latest);
    }

    public static void RequireDate(CommandContext context, DateOnly date, Gathered gathered)
    {
        if (date > BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow) || date < gathered.LatestDate)
        {
            throw new DomainException(ImportErrors.SettlementInvalid, $"The settlement date is not in the future nor before its latest document ({gathered.LatestDate:yyyy-MM-dd}).");
        }
    }

    public static string? Reference(string? reference)
    {
        var text = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        return text is { Length: > 80 } ? throw new DomainException(ImportErrors.SettlementInvalid, "The reference has at most 80 characters.") : text;
    }

    public static async Task WriteRowsAsync(CommandContext context, Guid settlementId, Gathered gathered, CancellationToken cancellationToken)
    {
        foreach (var (kind, id, cost) in gathered.Documents)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO pur.import_settlement_document (settlement_id, company_id, document_kind, document_id, cost_amount) VALUES (@s, @c, @k, @d, @cost)",
                cancellationToken,
                ("s", settlementId),
                ("c", context.CompanyId),
                ("k", kind),
                ("d", id),
                ("cost", cost)).ConfigureAwait(false);
        }

        foreach (var (line, value, added) in gathered.Allocation)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO pur.import_settlement_allocation (settlement_id, company_id, target_kind, target_id, base_value, added_cost) VALUES (@s, @c, 'EXPENSE_LINE', @l, @b, @a)",
                cancellationToken,
                ("s", settlementId),
                ("c", context.CompanyId),
                ("l", line),
                ("b", value),
                ("a", added)).ConfigureAwait(false);
        }
    }

    public static async Task DeleteRowsAsync(CommandContext context, Guid settlementId, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "DELETE FROM pur.import_settlement_allocation WHERE settlement_id = @s", cancellationToken, ("s", settlementId))
            .ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "DELETE FROM pur.import_settlement_document WHERE settlement_id = @s", cancellationToken, ("s", settlementId))
            .ConfigureAwait(false);
    }

    public sealed record Header(Guid Id, string Number, Guid PlantId, DateOnly Date, string Status, Guid PreparedBy, long Version, Guid? PostingEventId);

    public static async Task<Header> LockAsync(CommandContext context, Guid settlementId, long expectedVersion, CancellationToken cancellationToken)
    {
        var header = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT settlement_id, settlement_no, plant_id, settlement_date, status, prepared_by, version, posting_event_id FROM pur.import_settlement WHERE company_id = @c AND settlement_id = @s FOR UPDATE",
            r => new Header(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.Date(3), r.GetString(4), r.GetGuid(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetGuid(7)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", settlementId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(ImportErrors.NotFound, "The settlement does not exist.");
        return header.Version == expectedVersion
            ? header
            : throw new DomainException(ImportErrors.VersionConflict, $"The settlement is at version {header.Version}, not {expectedVersion}.");
    }

    public static void RequireStatus(Header header, string status)
    {
        if (header.Status != status)
        {
            throw new DomainException(ImportErrors.InvalidState, $"The settlement is {header.Status}, not {status}.");
        }
    }

    public static object Payload(Gathered g) => new
    {
        documents = g.Documents.Select(d => new { kind = d.Kind, documentId = d.Id, cost = CustomsDeclarations.Text(d.Cost) }),
        allocation = g.Allocation.Select(a => new { siLineId = a.LineId, baseValue = CustomsDeclarations.Text(a.Base), addedCost = CustomsDeclarations.Text(a.Added) }),
        totalCost = CustomsDeclarations.Text(g.Allocation.Sum(a => a.Added)),
    };
}

[RequiresPermission("import_settlement:prepare")]
public sealed class PrepareImportSettlementHandler : ICommandHandler<PrepareImportSettlement>
{
    public string CommandType => "Procurement.PrepareImportSettlement";

    public async Task<string> HandleAsync(PrepareImportSettlement command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reference = ImportSettlements.Reference(command.Reference);
        var gathered = await ImportSettlements.GatherAsync(context, command.PlantId, null, command.Documents, cancellationToken).ConfigureAwait(false);
        ImportSettlements.RequireDate(context, command.SettlementDate, gathered);
        var preparer = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var id = context.ResultRef;
        var number = await DocumentNumbers.NextAsync(context, ImportSettlements.Prefix, command.SettlementDate.Year, cancellationToken).ConfigureAwait(false);
        var payload = ImportSettlements.Payload(gathered);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ImportSettlementPrepared", 1, ImportSettlements.Aggregate, id, 1,
                JsonSerializer.Serialize(new { settlementId = id, settlementNo = number, plantId = command.PlantId, settlementDate = command.SettlementDate, reference, detail = payload }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.import_settlement (settlement_id, company_id, settlement_no, plant_id, settlement_date, reference, status, accounting_status, prepared_by, version)
            VALUES (@id, @c, @no, @plant, @date, @ref, 'DRAFT', 'NOT_POSTED', @by, 1)
            """,
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("no", number),
            ("plant", command.PlantId),
            ("date", command.SettlementDate),
            ("ref", reference),
            ("by", preparer)).ConfigureAwait(false);
        await context.AppendStateAsync(ImportSettlements.Aggregate, id, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await ImportSettlements.WriteRowsAsync(context, id, gathered, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { settlementId = id, settlementNo = number, status = "DRAFT", version = 1, detail = payload });
    }
}

[RequiresPermission("import_settlement:prepare")]
public sealed class UpdateImportSettlementDraftHandler : ICommandHandler<UpdateImportSettlementDraft>
{
    public string CommandType => "Procurement.UpdateImportSettlementDraft";

    public async Task<string> HandleAsync(UpdateImportSettlementDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reference = ImportSettlements.Reference(command.Reference);
        var header = await ImportSettlements.LockAsync(context, command.SettlementId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        ImportSettlements.RequireStatus(header, "DRAFT");
        var gathered = await ImportSettlements.GatherAsync(context, header.PlantId, header.Id, command.Documents, cancellationToken).ConfigureAwait(false);
        ImportSettlements.RequireDate(context, command.SettlementDate, gathered);
        var version = header.Version + 1;
        var payload = ImportSettlements.Payload(gathered);
        await context.AppendEventAsync(
            new EventDraft(
                "ImportSettlementDraftUpdated", 1, ImportSettlements.Aggregate, header.Id, version,
                JsonSerializer.Serialize(new { settlementId = header.Id, settlementDate = command.SettlementDate, reference, detail = payload }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await ImportSettlements.DeleteRowsAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        await ImportSettlements.WriteRowsAsync(context, header.Id, gathered, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE pur.import_settlement SET settlement_date = @d, reference = @r, version = @v WHERE settlement_id = @s", cancellationToken,
            ("d", command.SettlementDate), ("r", reference), ("v", version), ("s", header.Id)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { settlementId = header.Id, status = "DRAFT", version, detail = payload });
    }
}

[RequiresPermission("import_settlement:prepare")]
public sealed class CancelImportSettlementHandler : ICommandHandler<CancelImportSettlement>
{
    public string CommandType => "Procurement.CancelImportSettlement";

    public async Task<string> HandleAsync(CancelImportSettlement command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await ImportSettlements.LockAsync(context, command.SettlementId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        ImportSettlements.RequireStatus(header, "DRAFT");
        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("ImportSettlementCancelled", 1, ImportSettlements.Aggregate, header.Id, version, JsonSerializer.Serialize(new { settlementId = header.Id }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await ImportSettlements.DeleteRowsAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE pur.import_settlement SET status = 'CANCELLED', version = @v WHERE settlement_id = @s", cancellationToken,
            ("v", version), ("s", header.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(ImportSettlements.Aggregate, header.Id, "DOCUMENT", "DRAFT", "CANCELLED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { settlementId = header.Id, status = "CANCELLED", version });
    }
}

[RequiresPermission("import_settlement:approve", StepUp = true)]
public sealed class ApproveImportSettlementHandler : ICommandHandler<ApproveImportSettlement>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Procurement.ApproveImportSettlement";

    public async Task<string> HandleAsync(ApproveImportSettlement command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await ImportSettlements.LockAsync(context, command.SettlementId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        ImportSettlements.RequireStatus(header, "DRAFT");
        var approver = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == header.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ImportErrors.InvalidState, "A settlement is approved by someone other than who prepared it (E-USD1-01-7).");
        }

        // The documents cannot have been reversed while in this draft (E-USD1-04-7): gathering them again yields the stored allocation.
        var documents = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT document_kind, document_id FROM pur.import_settlement_document WHERE settlement_id = @s ORDER BY document_kind, document_id",
            r => new ImportSettlementDocumentInput(r.GetString(0), r.GetGuid(1)),
            cancellationToken,
            ("s", header.Id)).ConfigureAwait(false);
        var gathered = await ImportSettlements.GatherAsync(context, header.PlantId, header.Id, documents, cancellationToken).ConfigureAwait(false);

        var inputs = new List<PostingLineInput>();
        var allocation = gathered.Allocation.ToDictionary(a => a.LineId, a => a.Added);
        foreach (var line in gathered.Goods)
        {
            inputs.Add(new PostingLineInput(
                "P40-DR-COST", "added_cost", allocation[line.LineId], PlantId: header.PlantId, PartyId: line.PartyId, AccountId: line.AccountId,
                Inputs: new Dictionary<string, string>
                {
                    ["settlement"] = header.Number,
                    ["description"] = line.Description,
                    ["category"] = line.Category,
                    ["number"] = line.Number,
                    ["si_line_id"] = line.LineId.ToString(),
                }));
        }

        foreach (var dua in gathered.Duas)
        {
            inputs.Add(new PostingLineInput(
                "P40-CR-CLR", "dua_cost", dua.Cost, PlantId: header.PlantId, PartyId: dua.PartyId, SubledgerRef: dua.Id,
                Inputs: new Dictionary<string, string> { ["settlement"] = header.Number, ["dua"] = dua.Number, ["dua_id"] = dua.Id.ToString() }));
        }

        foreach (var line in gathered.Expenses)
        {
            inputs.Add(new PostingLineInput(
                "P40-CR-EXP", "expense_cost", line.Net, PlantId: header.PlantId, PartyId: line.PartyId, AccountId: line.AccountId,
                Inputs: new Dictionary<string, string>
                {
                    ["settlement"] = header.Number,
                    ["description"] = line.Description,
                    ["category"] = line.Category,
                    ["number"] = line.Number,
                    ["si_line_id"] = line.LineId.ToString(),
                }));
        }

        var occurredAt = context.Clock.UtcNow;
        var plan = await _engine.PrepareAsync(context, new PostingRequest(ImportSettlements.P40, header.Date, occurredAt, inputs), cancellationToken).ConfigureAwait(false);
        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ImportSettlementPosted", 1, ImportSettlements.Aggregate, header.Id, version,
                JsonSerializer.Serialize(new { settlementId = header.Id, settlementNo = header.Number, approvedBy = approver, detail = ImportSettlements.Payload(gathered) }),
                Publish: true, OccurredAt: occurredAt, BusinessDate: header.Date),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE pur.import_settlement SET status = 'POSTED', accounting_status = 'POSTED', posting_event_id = @e, approved_by = @by, approved_at = @at, version = @v
            WHERE settlement_id = @s
            """,
            cancellationToken,
            ("e", eventId),
            ("by", approver),
            ("at", occurredAt),
            ("v", version),
            ("s", header.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(ImportSettlements.Aggregate, header.Id, "DOCUMENT", "DRAFT", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            settlementId = header.Id,
            settlementNo = header.Number,
            status = "POSTED",
            totalCost = CustomsDeclarations.Text(gathered.Allocation.Sum(a => a.Added)),
            journals = new[] { journal.JournalId },
            version,
        });
    }
}

[RequiresPermission("import_settlement:approve", StepUp = true)]
public sealed class ReverseImportSettlementHandler : ICommandHandler<ReverseImportSettlement>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Procurement.ReverseImportSettlement";

    public async Task<string> HandleAsync(ReverseImportSettlement command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await ImportSettlements.LockAsync(context, command.SettlementId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        ImportSettlements.RequireStatus(header, "POSTED");
        var journalId = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT journal_id FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'AUTO'",
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", header.PostingEventId)).ConfigureAwait(false)).Single();
        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var plan = await _engine.PrepareReversalAsync(context, journalId, businessDate, cancellationToken).ConfigureAwait(false);
        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ImportSettlementReversed", 1, ImportSettlements.Aggregate, header.Id, version,
                JsonSerializer.Serialize(new { settlementId = header.Id, reversedJournalId = journalId, reason }), Publish: true, OccurredAt: occurredAt, BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE pur.import_settlement SET status = 'REVERSED', accounting_status = 'REVERSED', version = @v WHERE settlement_id = @s",
            cancellationToken, ("v", version), ("s", header.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(ImportSettlements.Aggregate, header.Id, "DOCUMENT", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, occurredAt, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { settlementId = header.Id, status = "REVERSED", journals = new[] { reversal.JournalId }, version });
    }
}
