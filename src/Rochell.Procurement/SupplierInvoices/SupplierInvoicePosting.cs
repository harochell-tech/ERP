using System.Globalization;
using System.Text.Json;
using Rochell.Finance;
using Rochell.Finance.Policies;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Tax;

namespace Rochell.Procurement.SupplierInvoices;

/// <summary>A PO line billed by an invoice line, read after locking.</summary>
internal sealed record BilledPoLine(Guid PoLineId, Guid PoId, Guid PlantId, Guid ValuationAreaId, Guid ItemId, decimal UnitPrice, decimal QtyReceived, decimal QtyInvoiced);

/// <summary>The price-difference evidence of one invoice line, kept in the posting event for its reversal (R-07).</summary>
internal sealed record PriceDifference(Guid SiLineId, Guid PlantId, Guid ValuationAreaId, Guid ItemId, decimal Difference, decimal BaseQuantity, decimal ItemQuantity, decimal Covered, Guid? ValueEntryId);

internal static class InvoicePostingStore
{
    public const string R04 = "R-04";
    public const string R05 = "R-05";
    public const string R07B = "R-07B";

    /// <summary>Lock order: PO headers (N3) by id, then PO lines (N4) by id; values read once the locks are held.</summary>
    public static async Task<Dictionary<Guid, BilledPoLine>> LockPoLinesAsync(CommandContext context, IEnumerable<Guid> poLineIds, CancellationToken cancellationToken)
    {
        var ids = poLineIds.Distinct().OrderBy(i => i).ToArray();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT po.po_id FROM pur.purchase_order po
            WHERE po.po_id IN (SELECT po_id FROM pur.purchase_order_line WHERE po_line_id = ANY(@ids))
            ORDER BY po.po_id
            FOR UPDATE
            """,
            cancellationToken,
            ("ids", ids)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT po_line_id FROM pur.purchase_order_line WHERE po_line_id = ANY(@ids) ORDER BY po_line_id FOR UPDATE",
            cancellationToken,
            ("ids", ids)).ConfigureAwait(false);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT pol.po_line_id, pol.po_id, po.plant_id, p.valuation_area_id, pol.item_id, pol.unit_price, pol.qty_received, pol.qty_invoiced
            FROM pur.purchase_order_line pol
            JOIN pur.purchase_order po ON po.po_id = pol.po_id
            JOIN md.plant p ON p.plant_id = po.plant_id
            WHERE pol.po_line_id = ANY(@ids)
            """,
            ("ids", ids));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var lines = new Dictionary<Guid, BilledPoLine>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var line = new BilledPoLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7));
            lines[line.PoLineId] = line;
        }

        return lines;
    }

    /// <summary>E-PR13-7: the receipts' own conversion (base quantity ÷ PO quantity) of a PO line.</summary>
    public static async Task<decimal> ReceiptFactorAsync(CommandContext context, Guid poLineId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT sum(q.quantity) / sum(gl.qty)
            FROM pur.goods_receipt_line gl
            JOIN inv.inv_quantity_entry q ON q.source_line_id = gl.gr_line_id AND q.movement_type = 'RECEIPT'
            WHERE gl.po_line_id = @l
            """,
            ("l", poLineId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as decimal?
            ?? throw new InvalidOperationException("A billed PO line without receipts.");
    }

    public static async Task<Guid?> AutoJournalAsync(CommandContext context, Guid sourceEventId, string ruleCode, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT j.journal_id FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id
            WHERE j.company_id = @c AND j.source_event_id = @e AND r.code = @rule AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal x WHERE x.reverses_journal_id = j.journal_id)
            """,
            ("c", context.CompanyId),
            ("e", sourceEventId),
            ("rule", ruleCode));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    public static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Share s of a price difference allocated to stock, as recorded for Explain (six decimals).</summary>
    public static string Coverage(decimal allocated, decimal difference)
        => (difference == 0 ? 0 : decimal.Round(allocated / difference, 6, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
}

[RequiresPermission("supplier_invoice:post")]
public sealed class PostSupplierInvoiceHandler : ICommandHandler<PostSupplierInvoice>
{
    /// <summary>The INVENTORY policy that allocates price differences; STOCK_COVERAGE is the only method in VS#1 (Patch 1.1).</summary>
    internal static async Task<ResolvedPolicy> AllocationPolicyAsync(CommandContext context, DateOnly date, CancellationToken cancellationToken)
    {
        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Inventory, date, cancellationToken).ConfigureAwait(false);
        var method = policy.Text(PolicyParameters.InvoicePriceVarianceAllocationMethod);
        return method == "STOCK_COVERAGE"
            ? policy
            : throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"Price-difference allocation method {method} is not implemented; VS#1 supports STOCK_COVERAGE.");
    }

    /// <summary>
    /// POL-01: what R-05 used for one line — method, policy version, area quantity, the line's Q, the invoice's Q of the item
    /// (E-VS1-11), s and D.
    /// </summary>
    private static Dictionary<string, string> AllocationInputs(ResolvedPolicy policy, decimal areaQuantity, decimal baseQuantity, decimal itemQuantity, decimal difference)
        => new()
        {
            [PolicyParameters.InvoicePriceVarianceAllocationMethod] = policy.Text(PolicyParameters.InvoicePriceVarianceAllocationMethod),
            ["policy_version_id"] = policy.PolicyVersionId.ToString(),
            ["area_quantity"] = areaQuantity.ToString(CultureInfo.InvariantCulture),
            ["base_quantity"] = baseQuantity.ToString(CultureInfo.InvariantCulture),
            ["invoice_item_quantity"] = itemQuantity.ToString(CultureInfo.InvariantCulture),
            ["coverage"] = decimal.Round(Math.Min(areaQuantity, itemQuantity) / itemQuantity, 6, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture),
            ["difference"] = InvoicePostingStore.Text(difference),
        };

    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();
    private readonly TaxEngine _tax = new();

    public string CommandType => "Procurement.PostSupplierInvoice";

    public async Task<string> HandleAsync(PostSupplierInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // N3: the invoice.
        var header = await SupplierInvoiceStore.LockAsync(context, command.SupplierInvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status != SupplierInvoiceStatus.Matched || header.AccountingStatus is not ("NOT_POSTED" or "POSTING_BLOCKED"))
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The invoice is {header.Status}/{header.AccountingStatus}; only unposted MATCHED invoices can be posted.");
        }

        var lines = await SupplierInvoiceStore.LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        var po = await InvoicePostingStore.LockPoLinesAsync(context, lines.Select(l => l.PurchaseOrderLineId), cancellationToken).ConfigureAwait(false);

        // CC-03: re-checked under lock — a receipt correction may have reduced what was received since the match.
        foreach (var line in lines.Where(l => l.Quantity > po[l.PurchaseOrderLineId].QtyReceived - po[l.PurchaseOrderLineId].QtyInvoiced))
        {
            throw new DomainException(ProcurementErrors.QtyExceedsAvailable, $"Line {line.LineNo} bills more than is received and not yet invoiced; match the invoice again.");
        }

        // Fiscal gate and determination at the invoice date (E-PR13-5); a closed gate rejects (SI-07).
        var determination = await _tax.DetermineAsync(
            context,
            new TaxRequest("SupplierInvoice", header.Id, header.DocDate, header.PartyId, lines.Select(l => new TaxLineInput(l.Id, po[l.PurchaseOrderLineId].ItemId, l.NetAmount)).ToList()),
            cancellationToken).ConfigureAwait(false);

        if (determination.HasNonRecoverableInput)
        {
            // E-PR13-3 / SI-08: recorded, not posted — no journal, no AP, invoiced quantities untouched.
            var blockedVersion = header.Version + 1;
            await context.AppendEventAsync(
                new EventDraft("SupplierInvoicePostingBlocked", 1, SupplierInvoiceStore.Aggregate, header.Id, blockedVersion,
                    JsonSerializer.Serialize(new { siId = header.Id, determinationId = determination.DeterminationId, reason = "NON_RECOVERABLE_INPUT" }), Publish: true),
                cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.supplier_invoice SET accounting_status = 'POSTING_BLOCKED', tax_determination_id = @d, version = @v WHERE si_id = @s",
                cancellationToken,
                ("d", determination.DeterminationId),
                ("v", blockedVersion),
                ("s", header.Id)).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, accountingStatus = "POSTING_BLOCKED", determinationId = determination.DeterminationId, version = blockedVersion });
        }

        // Amounts: GRNI at PO price; D = invoiced net − GRNI; T and W from the determination.
        var occurredAt = context.Clock.UtcNow;
        var apDocId = context.Ids.NewId();
        var grni = lines.ToDictionary(l => l.Id, l => InvoicePostingStore.Money(l.Quantity * po[l.PurchaseOrderLineId].UnitPrice));
        var recoverable = determination.Taxes.Where(t => t.Effect == TaxEffects.RecoverableInput).ToList();
        var withholding = determination.Withholding;
        var totalGrni = grni.Values.Sum();
        var totalItbis = recoverable.Sum(t => t.Amount);
        var payableAtPoPrice = totalGrni + totalItbis - withholding;

        // R-05 (E-PR13-7, E-VS1-11): coverage s = min(area quantity, Q) ÷ Q, where Q is the invoice's total base quantity of the
        // item in the area (all its lines), so one invoice never capitalizes more price difference than the stock covers.
        // Valuation locked (N7) by area, item.
        var baseQuantities = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            var factor = await InvoicePostingStore.ReceiptFactorAsync(context, po[line.PurchaseOrderLineId].PoLineId, cancellationToken).ConfigureAwait(false);
            baseQuantities[line.Id] = decimal.Round(line.Quantity * factor, 6, MidpointRounding.AwayFromZero);
        }

        var itemQuantities = lines
            .GroupBy(l => (po[l.PurchaseOrderLineId].ValuationAreaId, po[l.PurchaseOrderLineId].ItemId))
            .ToDictionary(g => g.Key, g => g.Sum(l => baseQuantities[l.Id]));
        var differences = new List<PriceDifference>();
        var areaQuantities = new Dictionary<(Guid Area, Guid Item), decimal>();
        foreach (var line in lines.OrderBy(l => po[l.PurchaseOrderLineId].ValuationAreaId).ThenBy(l => po[l.PurchaseOrderLineId].ItemId))
        {
            var d = line.NetAmount - grni[line.Id];
            if (d == 0)
            {
                continue;
            }

            var b = po[line.PurchaseOrderLineId];
            if (!areaQuantities.TryGetValue((b.ValuationAreaId, b.ItemId), out var areaQuantity))
            {
                areaQuantity = (await _inventory.LockValuationAsync(context, b.ValuationAreaId, b.ItemId, cancellationToken).ConfigureAwait(false)).Quantity;
                areaQuantities[(b.ValuationAreaId, b.ItemId)] = areaQuantity;
            }

            var itemQuantity = itemQuantities[(b.ValuationAreaId, b.ItemId)];
            var covered = InvoicePostingStore.Money(d * Math.Min(areaQuantity, itemQuantity) / itemQuantity);
            differences.Add(new PriceDifference(line.Id, b.PlantId, b.ValuationAreaId, b.ItemId, d, baseQuantities[line.Id], itemQuantity, covered, covered == 0 ? null : context.Ids.NewId()));
        }

        // POL-01 (Patch 1.1, E-PR17-4): a price difference is allocated by the INVENTORY policy's method, recorded per line.
        ResolvedPolicy? allocationPolicy = null;
        if (differences.Count > 0)
        {
            allocationPolicy = await AllocationPolicyAsync(context, header.DocDate, cancellationToken).ConfigureAwait(false);
        }

        // P-1: every posting prerequisite before any write.
        var r04Lines = new List<PostingLineInput>();
        r04Lines.AddRange(lines.Select(l => new PostingLineInput("R04-DR-GRNI", "received_value", grni[l.Id], PlantId: po[l.PurchaseOrderLineId].PlantId, PartyId: header.PartyId)));
        r04Lines.AddRange(recoverable
            .GroupBy(t => po[lines.Single(l => l.Id == t.LineId).PurchaseOrderLineId].PlantId)
            .Select(g => new PostingLineInput("R04-DR-ITBIS", "recoverable_itbis", g.Sum(t => t.Amount), PlantId: g.Key)));
        r04Lines.Add(new PostingLineInput("R04-CR-AP", "payable_at_po_price", payableAtPoPrice, PartyId: header.PartyId, SubledgerRef: apDocId));
        r04Lines.Add(new PostingLineInput("R04-CR-WHT", "withholding", withholding, PartyId: header.PartyId));
        var r04 = await _engine.PrepareAsync(context, new PostingRequest(InvoicePostingStore.R04, header.DocDate, occurredAt, r04Lines), cancellationToken).ConfigureAwait(false);

        PostingPlan? r05 = null;
        if (differences.Count > 0)
        {
            var r05Lines = new List<PostingLineInput>();
            foreach (var pd in differences)
            {
                var uncovered = pd.Difference - pd.Covered;
                var up = pd.Difference > 0;
                var inputs = AllocationInputs(allocationPolicy!, areaQuantities[(pd.ValuationAreaId, pd.ItemId)], pd.BaseQuantity, pd.ItemQuantity, pd.Difference);
                if (pd.Covered != 0)
                {
                    r05Lines.Add(new PostingLineInput(up ? "R05-DR-INV" : "R05-CR-INV", "covered_difference", Math.Abs(pd.Covered), PlantId: pd.PlantId, ItemId: pd.ItemId, SubledgerRef: pd.ValueEntryId, InvValueEntryId: pd.ValueEntryId, Inputs: inputs));
                }

                r05Lines.Add(new PostingLineInput(up ? "R05-DR-PPV" : "R05-CR-PPV", "uncovered_difference", Math.Abs(uncovered), PlantId: pd.PlantId, ItemId: pd.ItemId, Inputs: inputs));
            }

            var totalDifference = differences.Sum(pd => pd.Difference);
            r05Lines.Add(new PostingLineInput(
                totalDifference >= 0 ? "R05-CR-AP" : "R05-DR-AP",
                "price_difference",
                Math.Abs(totalDifference),
                PartyId: header.PartyId,
                SubledgerRef: apDocId,
                Inputs: new Dictionary<string, string>
                {
                    [PolicyParameters.InvoicePriceVarianceAllocationMethod] = allocationPolicy!.Text(PolicyParameters.InvoicePriceVarianceAllocationMethod),
                    ["policy_version_id"] = allocationPolicy.PolicyVersionId.ToString(),
                    ["difference"] = InvoicePostingStore.Text(totalDifference),
                }));
            r05 = await _engine.PrepareAsync(context, new PostingRequest(InvoicePostingStore.R05, header.DocDate, occurredAt, r05Lines), cancellationToken).ConfigureAwait(false);
        }

        // Writes.
        var total = lines.Sum(l => l.NetAmount) + totalItbis - withholding;
        var newVersion = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierInvoicePosted",
                1,
                SupplierInvoiceStore.Aggregate,
                header.Id,
                newVersion,
                JsonSerializer.Serialize(new
                {
                    siId = header.Id,
                    apDocId,
                    determinationId = determination.DeterminationId,
                    payable = InvoicePostingStore.Text(total),
                    itbis = InvoicePostingStore.Text(totalItbis),
                    withholding = InvoicePostingStore.Text(withholding),
                    priceDifferences = differences.Select(pd => new
                    {
                        siLineId = pd.SiLineId,
                        plantId = pd.PlantId,
                        valuationAreaId = pd.ValuationAreaId,
                        itemId = pd.ItemId,
                        difference = InvoicePostingStore.Text(pd.Difference),
                        baseQuantity = InvoicePostingStore.Text(pd.BaseQuantity),
                        itemQuantity = InvoicePostingStore.Text(pd.ItemQuantity),
                        covered = InvoicePostingStore.Text(pd.Covered),
                        valueEntryId = pd.ValueEntryId,
                    }),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: header.DocDate),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.supplier_invoice SET accounting_status = 'POSTED', tax_determination_id = @d, posting_event_id = @e, version = @v WHERE si_id = @s",
            cancellationToken,
            ("d", determination.DeterminationId),
            ("e", eventId),
            ("v", newVersion),
            ("s", header.Id)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.ap_document (ap_doc_id, company_id, party_id, doc_type, source_doc_id, doc_date, due_date, original_amount, open_amount, version)
            SELECT @id, company_id, party_id, 'SUPPLIER_INVOICE', si_id, doc_date, due_date, @amount, @amount, 1 FROM pur.supplier_invoice WHERE si_id = @s
            """,
            cancellationToken,
            ("id", apDocId),
            ("amount", total),
            ("s", header.Id)).ConfigureAwait(false);

        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.purchase_order_line SET qty_invoiced = qty_invoiced + @q, version = version + 1 WHERE po_line_id = @l",
                cancellationToken,
                ("q", line.Quantity),
                ("l", line.PurchaseOrderLineId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO core.document_link (link_id, company_id, from_type, from_id, from_line_id, to_type, to_id, to_line_id, link_type, qty, amount, event_id)
                VALUES (@id, @c, 'SupplierInvoice', @si, @sil, 'PurchaseOrder', @po, @pol, 'BILLS', @qty, @amount, @event)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("si", header.Id),
                ("sil", line.Id),
                ("po", po[line.PurchaseOrderLineId].PoId),
                ("pol", line.PurchaseOrderLineId),
                ("qty", line.Quantity),
                ("amount", line.NetAmount),
                ("event", eventId)).ConfigureAwait(false);
        }

        var journals = new List<Guid> { (await _engine.WriteAsync(context, r04, eventId, cancellationToken).ConfigureAwait(false)).JournalId };
        if (r05 is not null)
        {
            var dates = new MovementDates(occurredAt, header.DocDate, r05.PostingDate);
            foreach (var pd in differences.Where(pd => pd.ValueEntryId is not null))
            {
                await _inventory.PostValueAdjustmentAsync(
                    context, MovementTypes.PriceAdjustment, pd.ValuationAreaId, pd.PlantId, pd.ItemId, pd.Covered, pd.ValueEntryId!.Value, null,
                    new MovementSource(eventId, "SUPPLIER_INVOICE", header.Id, pd.SiLineId), dates, cancellationToken).ConfigureAwait(false);
            }

            journals.Add((await _engine.WriteAsync(context, r05, eventId, cancellationToken).ConfigureAwait(false)).JournalId);
        }

        return JsonSerializer.Serialize(new
        {
            supplierInvoiceId = header.Id,
            accountingStatus = "POSTED",
            apDocumentId = apDocId,
            payable = InvoicePostingStore.Text(total),
            journals,
            version = newVersion,
        });
    }
}

/// <summary>
/// R-07 (Patch 1 P-4): exact reversal of the R-04 and R-05 journals and of each R-05 value entry; R-07B moves
/// (s − s′)·D between inventory and PPV, with s′ the coverage by the stock at the reversal date.
/// </summary>
[RequiresPermission("supplier_invoice:reverse", StepUp = true)]
public sealed class ReverseSupplierInvoiceHandler : ICommandHandler<ReverseSupplierInvoice>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Procurement.ReverseSupplierInvoice";

    public async Task<string> HandleAsync(ReverseSupplierInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await SupplierInvoiceStore.LockAsync(context, command.SupplierInvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status != SupplierInvoiceStatus.Matched || header.AccountingStatus != "POSTED")
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The invoice is {header.Status}/{header.AccountingStatus}; only posted invoices can be reversed.");
        }

        Guid apDocId;
        Guid postingEventId;
        JsonElement posting;
        await using (var ap = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT a.ap_doc_id, a.open_amount = a.original_amount, si.posting_event_id, e.payload::text
            FROM fin.ap_document a
            JOIN pur.supplier_invoice si ON si.si_id = a.source_doc_id
            JOIN core.domain_event e ON e.event_id = si.posting_event_id
            WHERE a.source_doc_id = @s
            FOR UPDATE OF a
            """,
            ("s", header.Id)))
        await using (var reader = await ap.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            apDocId = reader.GetGuid(0);
            if (!reader.GetBoolean(1))
            {
                throw new DomainException(ProcurementErrors.ApNotOpen, "The AP document has applications; it can no longer be reversed.");
            }

            postingEventId = reader.GetGuid(2);
            posting = JsonDocument.Parse(reader.GetString(3)).RootElement.Clone();
        }

        var lines = await SupplierInvoiceStore.LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        await InvoicePostingStore.LockPoLinesAsync(context, lines.Select(l => l.PurchaseOrderLineId), cancellationToken).ConfigureAwait(false);

        // R-07B needs s′ (coverage now) per price difference; valuation locked (N7).
        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var reversals = new List<(Guid Original, Guid Reversal, decimal Amount, Guid Area, Guid Plant, Guid Item)>();
        var reallocations = new List<(decimal Amount, Guid Area, Guid Plant, Guid Item, Guid ValueEntryId)>();
        var reallocationInputs = new List<Dictionary<string, string>>();
        foreach (var pd in posting.GetProperty("priceDifferences").EnumerateArray()
                     .OrderBy(p => p.GetProperty("valuationAreaId").GetGuid()).ThenBy(p => p.GetProperty("itemId").GetGuid()))
        {
            var area = pd.GetProperty("valuationAreaId").GetGuid();
            var plant = pd.GetProperty("plantId").GetGuid();
            var item = pd.GetProperty("itemId").GetGuid();
            var difference = decimal.Parse(pd.GetProperty("difference").GetString()!, CultureInfo.InvariantCulture);
            var baseQuantity = decimal.Parse(pd.GetProperty("baseQuantity").GetString()!, CultureInfo.InvariantCulture);
            var covered = decimal.Parse(pd.GetProperty("covered").GetString()!, CultureInfo.InvariantCulture);

            // E-VS1-11: the Q the posting used (invoices posted before it recorded only the line's quantity).
            var itemQuantity = pd.TryGetProperty("itemQuantity", out var q) ? decimal.Parse(q.GetString()!, CultureInfo.InvariantCulture) : baseQuantity;
            var areaQuantity = (await _inventory.LockValuationAsync(context, area, item, cancellationToken).ConfigureAwait(false)).Quantity;

            // E-VS1-6 (#24): s′ = min(s, min(area quantity, Q) ÷ Q) — never move back more than R-05 capitalized.
            var coveredNow = InvoicePostingStore.Money(difference * Math.Min(areaQuantity, itemQuantity) / itemQuantity);
            if (Math.Abs(coveredNow) > Math.Abs(covered))
            {
                coveredNow = covered;
            }
            if (pd.GetProperty("valueEntryId").ValueKind == JsonValueKind.String)
            {
                reversals.Add((pd.GetProperty("valueEntryId").GetGuid(), context.Ids.NewId(), -covered, area, plant, item));
            }

            var reallocation = covered - coveredNow;
            if (reallocation != 0)
            {
                reallocations.Add((reallocation, area, plant, item, context.Ids.NewId()));
                reallocationInputs.Add(new Dictionary<string, string>
                {
                    ["difference"] = InvoicePostingStore.Text(difference),
                    ["base_quantity"] = baseQuantity.ToString(CultureInfo.InvariantCulture),
                    ["invoice_item_quantity"] = itemQuantity.ToString(CultureInfo.InvariantCulture),
                    ["area_quantity"] = areaQuantity.ToString(CultureInfo.InvariantCulture),
                    ["coverage_original"] = InvoicePostingStore.Coverage(covered, difference),
                    ["coverage_now"] = InvoicePostingStore.Coverage(coveredNow, difference),
                });
            }
        }

        ResolvedPolicy? allocationPolicy = reallocations.Count > 0
            ? await PostSupplierInvoiceHandler.AllocationPolicyAsync(context, businessDate, cancellationToken).ConfigureAwait(false)
            : null;

        // P-1: every prerequisite before any write.
        var r04Journal = await InvoicePostingStore.AutoJournalAsync(context, postingEventId, InvoicePostingStore.R04, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A posted invoice without its R-04 journal (K-25 should prevent this).");
        var r05Journal = await InvoicePostingStore.AutoJournalAsync(context, postingEventId, InvoicePostingStore.R05, cancellationToken).ConfigureAwait(false);
        var r04Plan = await _engine.PrepareReversalAsync(context, r04Journal, businessDate, cancellationToken).ConfigureAwait(false);
        var r05Plan = r05Journal is null ? null : await _engine.PrepareReversalAsync(context, r05Journal.Value, businessDate, cancellationToken).ConfigureAwait(false);
        PostingPlan? r07b = null;
        if (reallocations.Count > 0)
        {
            r07b = await _engine.PrepareAsync(
                context,
                new PostingRequest(
                    InvoicePostingStore.R07B,
                    businessDate,
                    occurredAt,
                    reallocations.SelectMany((r, i) =>
                    {
                        var inputs = new Dictionary<string, string>(reallocationInputs[i])
                        {
                            [PolicyParameters.InvoicePriceVarianceAllocationMethod] = allocationPolicy!.Text(PolicyParameters.InvoicePriceVarianceAllocationMethod),
                            ["policy_version_id"] = allocationPolicy.PolicyVersionId.ToString(),
                        };
                        return r.Amount > 0
                            ? new[]
                            {
                                new PostingLineInput("R07B-DR-INV", "reallocation", r.Amount, PlantId: r.Plant, ItemId: r.Item, SubledgerRef: r.ValueEntryId, InvValueEntryId: r.ValueEntryId, Inputs: inputs),
                                new PostingLineInput("R07B-CR-PPV", "reallocation", r.Amount, PlantId: r.Plant, ItemId: r.Item, Inputs: inputs),
                            }
                            : new[]
                            {
                                new PostingLineInput("R07B-CR-INV", "reallocation", -r.Amount, PlantId: r.Plant, ItemId: r.Item, SubledgerRef: r.ValueEntryId, InvValueEntryId: r.ValueEntryId, Inputs: inputs),
                                new PostingLineInput("R07B-DR-PPV", "reallocation", -r.Amount, PlantId: r.Plant, ItemId: r.Item, Inputs: inputs),
                            };
                    }).ToList(),
                    JournalType: "VALUATION_REALLOCATION"),
                cancellationToken).ConfigureAwait(false);
        }

        // Writes.
        var newVersion = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierInvoiceReversed",
                1,
                SupplierInvoiceStore.Aggregate,
                header.Id,
                newVersion,
                JsonSerializer.Serialize(new
                {
                    siId = header.Id,
                    apDocId,
                    reason,
                    reallocations = reallocations.Select(r => new { itemId = r.Item, amount = InvoicePostingStore.Text(r.Amount) }),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(SupplierInvoiceStore.Aggregate, header.Id, "DOCUMENT", header.Status, SupplierInvoiceStatus.Reversed, CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.supplier_invoice SET document_status = 'REVERSED', accounting_status = 'REVERSED', version = @v WHERE si_id = @s",
            cancellationToken,
            ("v", newVersion),
            ("s", header.Id)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.ap_document SET open_amount = 0, version = version + 1 WHERE ap_doc_id = @a",
            cancellationToken,
            ("a", apDocId)).ConfigureAwait(false);
        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.purchase_order_line SET qty_invoiced = qty_invoiced - @q, version = version + 1 WHERE po_line_id = @l",
                cancellationToken,
                ("q", line.Quantity),
                ("l", line.PurchaseOrderLineId)).ConfigureAwait(false);
        }

        var source = new MovementSource(eventId, "SUPPLIER_INVOICE_REVERSAL", header.Id);
        var journals = new List<Guid> { (await _engine.WriteReversalAsync(context, r04Plan, eventId, occurredAt, cancellationToken).ConfigureAwait(false)).JournalId };
        if (r05Plan is not null)
        {
            var dates = new MovementDates(occurredAt, businessDate, r05Plan.PostingDate);
            foreach (var r in reversals)
            {
                await _inventory.PostValueAdjustmentAsync(context, MovementTypes.PriceAdjustment, r.Area, r.Plant, r.Item, r.Amount, r.Reversal, r.Original, source, dates, cancellationToken).ConfigureAwait(false);
            }

            journals.Add((await _engine.WriteReversalAsync(context, r05Plan, eventId, occurredAt, cancellationToken, reversals.ToDictionary(r => r.Original, r => r.Reversal)).ConfigureAwait(false)).JournalId);
        }

        if (r07b is not null)
        {
            var dates = new MovementDates(occurredAt, businessDate, r07b.PostingDate);
            foreach (var r in reallocations)
            {
                await _inventory.ReallocateValueAsync(context, r.Area, r.Plant, r.Item, r.Amount, r.ValueEntryId, source, dates, cancellationToken).ConfigureAwait(false);
            }

            journals.Add((await _engine.WriteAsync(context, r07b, eventId, cancellationToken).ConfigureAwait(false)).JournalId);
        }

        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status = SupplierInvoiceStatus.Reversed, journals, version = newVersion });
    }
}
