using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Finance.Policies;
using Rochell.Finance.Posting;
using Rochell.FixedAssets.Cards;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax;

namespace Rochell.Procurement.Expenses;

/// <summary>
/// GAS1-04 (E-GAS-04-1…7): the expense invoice without a purchase order. Registered with its own command; matched against the
/// approval amount of the PURCHASING policy (E-GAS-04-2/3); an exception is approved by the Controller as for any invoice; posted
/// with P-37 (each line to its category's account, the taxes of its type to their accounts, E-GAS-04-4); voided and reversed as any
/// supplier invoice (E-GAS-04-6/7).
/// </summary>
internal static class ExpenseInvoices
{
    public const string P37 = "P-37";
    public const string P38 = "P-38";
    public const string Usd = "USD";
    public const string ApprovalThreshold = "expense_invoice_approval_threshold";
    private const decimal MaxQuantity = 999_999_999_999.999999m; // type-limit: numeric(18,6)
    private const decimal MaxUnitPrice = 9_999_999_999_999.999999m; // type-limit: numeric(19,6)

    /// <summary>An expense line; a USD line (E-USD1-03-4) has no tax type and keeps its USD price and net besides the peso ones.</summary>
    public sealed record Line(
        Guid Id, int LineNo, string Description, decimal Quantity, decimal UnitPrice, decimal Net, Guid? TaxTypeId, Guid CategoryId, Guid AccountId, string Category, string Scope, Guid? PoLineId,
        decimal? UnitPriceFc = null, decimal? NetFc = null, string? IsrWithholdingType = null);

    public static async Task<IReadOnlyList<Line>> LinesAsync(CommandContext context, Guid siId, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.si_line_id, l.line_no, l.description, l.qty, l.unit_price, l.net_amount, l.tax_rule_id, c.expense_category_id, c.account_id, c.code,
                   CASE c.line_class WHEN 'SERVICE' THEN 'EXPENSE_SERVICE' ELSE 'EXPENSE_GOODS' END, l.po_line_id, l.unit_price_fc, l.net_amount_fc, c.isr_withholding_type
            FROM pur.supplier_invoice_line l JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
            WHERE l.si_id = @s ORDER BY l.line_no
            """,
            r => new Line(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetGuid(7), r.GetGuid(8), r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetGuid(11),
                r.IsDBNull(12) ? null : r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetDecimal(13), r.IsDBNull(14) ? null : r.GetString(14)),
            cancellationToken,
            ("s", siId)).ConfigureAwait(false);

    private static List<TaxLineInput> TaxLines(IEnumerable<Line> lines) => [.. lines.Select(l => new TaxLineInput(l.Id, null, l.Net, l.TaxTypeId, l.Scope, l.IsrWithholdingType))];

    /// <summary>E-X1-02-3: B for a series-B NCF, E for an e-CF; a foreign supplier's own number has no series.</summary>
    public static string? SeriesOf(string fiscalNumber) => fiscalNumber is ['B', ..] ? "B" : fiscalNumber is ['E', ..] && fiscalNumber.Length == 13 ? "E" : null;

    /// <summary>
    /// E-GAS-04-2/3: the invoice's total — the net plus every tax of its lines' types in force on its date — against the policy's
    /// approval amount: below it the invoice is MATCHED; from it, a MATCH_EXCEPTION the Controller approves.
    /// </summary>
    public static async Task<string> MatchAsync(CommandContext context, InvoiceHeader header, ResolvedPolicy policy, string commandType, CancellationToken cancellationToken)
    {
        var lines = await LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        if (lines.Any(l => l.PoLineId is not null))
        {
            return await MatchToOrderAsync(context, header, policy, lines, commandType, cancellationToken).ConfigureAwait(false);
        }

        var threshold = policy.Decimal(ApprovalThreshold);
        var taxes = 0m;
        if (header.Currency != Usd)
        {
            // E-USD1-03-3: a USD invoice has no taxes; its peso total at its rate is compared with the approval amount.
            var estimate = await TaxEngine.EstimateItbisAsync(context.Connection, context.Transaction, context.CompanyId, header.DocDate, false, TaxLines(lines), cancellationToken).ConfigureAwait(false);
            taxes = estimate.Total
                ?? throw new DomainException(estimate.UnavailableCode ?? TaxErrors.FiscalGateClosed, estimate.UnavailableReason ?? "The taxes of the invoice cannot be determined on its date.");
        }

        var total = lines.Sum(l => l.Net) + taxes;
        var matched = total < threshold;
        var status = matched ? SupplierInvoiceStatus.Matched : SupplierInvoiceStatus.MatchException;
        var version = await SupplierInvoiceStore.TransitionAsync(
            context,
            header,
            status,
            commandType,
            matched ? "SupplierInvoiceMatched" : "MatchExceptionRaised",
            new
            {
                siId = header.Id,
                status,
                docClass = SupplierInvoiceClasses.Expense,
                policyVersionId = policy.PolicyVersionId,
                total = Text(total),
                approvalThreshold = Text(threshold),
            },
            publish: !matched,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status, total = Text(total), approvalThreshold = Text(threshold), qtyExceeds = false, version });
    }

    /// <summary>E-GAS-04-4/5: P-37 and the AP document of a MATCHED expense invoice, in one transaction.</summary>
    public static async Task<string> PostAsync(CommandContext context, InvoiceHeader header, PostingEngine engine, TaxEngine tax, CancellationToken cancellationToken)
    {
        var lines = await LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);

        // E-GAS-05-2/3: re-checked under the order lines' locks — another invoice may have billed them since the match.
        foreach (var (line, open) in await OrderLinesAsync(context, lines, cancellationToken).ConfigureAwait(false))
        {
            if (line.Quantity > open)
            {
                throw new DomainException(ProcurementErrors.QtyExceedsAvailable, $"Line {line.LineNo} bills more than the order has still to bill ({open}); match the invoice again.");
            }
        }

        if (header.Currency == Usd)
        {
            return await PostForeignAsync(context, header, lines, engine, cancellationToken).ConfigureAwait(false);
        }

        var determination = await tax.DetermineAsync(
            context, new TaxRequest("SupplierInvoice", header.Id, header.DocDate, header.PartyId, TaxLines(lines), DocumentSeries: SeriesOf(header.FiscalNumber)), cancellationToken).ConfigureAwait(false);
        decimal Sum(string effect) => determination.Taxes.Where(t => t.Effect == effect).Sum(t => t.Amount);
        var (itbis, selective, other, tip, withholding) = (Sum(TaxEffects.RecoverableInput), Sum(TaxEffects.SelectiveTax), Sum(TaxEffects.OtherTax), Sum(TaxEffects.LegalTip), determination.Withholding);
        var net = lines.Sum(l => l.Net);
        var payable = net + itbis + selective + other + tip - withholding;
        var plant = header.PlantId!.Value;
        var apDocId = context.Ids.NewId();
        var occurredAt = context.Clock.UtcNow;
        var inputs = new List<PostingLineInput>();
        foreach (var line in lines)
        {
            inputs.Add(new PostingLineInput(
                "P37-DR-EXP", "expense_net", line.Net, PlantId: plant, PartyId: header.PartyId, AccountId: line.AccountId,
                Inputs: new Dictionary<string, string> { ["ncf"] = header.FiscalNumber, ["category"] = line.Category, ["description"] = line.Description, ["si_line_id"] = line.Id.ToString() }));
        }

        var ncf = new Dictionary<string, string> { ["ncf"] = header.FiscalNumber };
        inputs.Add(new PostingLineInput("P37-DR-ITBIS", "recoverable_itbis", itbis, PlantId: plant, Inputs: ncf));
        inputs.Add(new PostingLineInput("P37-DR-ISC", "selective_tax", selective, PlantId: plant, Inputs: ncf));
        inputs.Add(new PostingLineInput("P37-DR-OTHER", "other_tax", other, PlantId: plant, Inputs: ncf));
        inputs.Add(new PostingLineInput("P37-DR-TIP", "legal_tip", tip, PlantId: plant, Inputs: ncf));
        inputs.Add(new PostingLineInput("P37-CR-AP", "payable", payable, PartyId: header.PartyId, SubledgerRef: apDocId, Inputs: ncf));
        inputs.Add(new PostingLineInput("P37-CR-WHT", "withholding", withholding, PartyId: header.PartyId, Inputs: ncf));
        var plan = await engine.PrepareAsync(context, new PostingRequest(P37, header.DocDate, occurredAt, inputs), cancellationToken).ConfigureAwait(false);

        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ExpenseInvoicePosted",
                1,
                SupplierInvoiceStore.Aggregate,
                header.Id,
                version,
                JsonSerializer.Serialize(new
                {
                    siId = header.Id,
                    apDocId,
                    determinationId = determination.DeterminationId,
                    net = Text(net),
                    itbis = Text(itbis),
                    selectiveTax = Text(selective),
                    otherTax = Text(other),
                    legalTip = Text(tip),
                    withholding = Text(withholding),
                    payable = Text(payable),
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
            ("v", version),
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
            ("amount", payable),
            ("s", header.Id)).ConfigureAwait(false);
        await BillOrderAsync(context, header, lines, eventId, +1, "Procurement.PostSupplierInvoice", cancellationToken).ConfigureAwait(false);
        await FixedAssetCards.CreateForInvoiceAsync(context, header.Id, eventId, cancellationToken).ConfigureAwait(false); // E-AF-3
        var journal = await engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            supplierInvoiceId = header.Id,
            accountingStatus = "POSTED",
            apDocumentId = apDocId,
            payable = Text(payable),
            journals = new[] { journal.JournalId },
            version,
        });
    }

    /// <summary>
    /// E-USD1-03-5: P-38 and the USD AP document of a MATCHED foreign invoice — each line's peso net to its category's account (expense or
    /// fixed asset), the peso total to AP_FOREIGN with its USD amount. No taxes and no withholding (E-USD-3, E-USD1-03-8).
    /// </summary>
    private static async Task<string> PostForeignAsync(CommandContext context, InvoiceHeader header, IReadOnlyList<Line> lines, PostingEngine engine, CancellationToken cancellationToken)
    {
        var rate = header.ExchangeRate!.Value;
        var rateText = rate.ToString("0.0000", CultureInfo.InvariantCulture);
        var totalUsd = lines.Sum(l => l.NetFc!.Value);
        var payable = lines.Sum(l => l.Net);
        var plant = header.PlantId!.Value;
        var apDocId = context.Ids.NewId();
        var occurredAt = context.Clock.UtcNow;
        var inputs = new List<PostingLineInput>();
        foreach (var line in lines)
        {
            inputs.Add(new PostingLineInput(
                "P38-DR-EXP", "expense_net", line.Net, PlantId: plant, PartyId: header.PartyId, AccountId: line.AccountId,
                Inputs: new Dictionary<string, string>
                {
                    ["number"] = header.FiscalNumber,
                    ["category"] = line.Category,
                    ["description"] = line.Description,
                    ["amount_usd"] = Text(line.NetFc!.Value),
                    ["rate"] = rateText,
                    ["si_line_id"] = line.Id.ToString(),
                }));
        }

        inputs.Add(new PostingLineInput(
            "P38-CR-AP", "payable", payable, PartyId: header.PartyId, SubledgerRef: apDocId, AmountFc: totalUsd,
            Inputs: new Dictionary<string, string> { ["number"] = header.FiscalNumber, ["amount_usd"] = Text(totalUsd), ["rate"] = rateText }));
        var plan = await engine.PrepareAsync(context, new PostingRequest(P38, header.DocDate, occurredAt, inputs), cancellationToken).ConfigureAwait(false);

        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ForeignInvoicePosted",
                1,
                SupplierInvoiceStore.Aggregate,
                header.Id,
                version,
                JsonSerializer.Serialize(new { siId = header.Id, apDocId, currency = Usd, exchangeRate = rateText, totalUsd = Text(totalUsd), payable = Text(payable) }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: header.DocDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.supplier_invoice SET accounting_status = 'POSTED', posting_event_id = @e, version = @v WHERE si_id = @s",
            cancellationToken,
            ("e", eventId),
            ("v", version),
            ("s", header.Id)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.ap_document (ap_doc_id, company_id, party_id, doc_type, source_doc_id, doc_date, due_date, original_amount, open_amount, version,
              currency, original_amount_fc, open_amount_fc)
            SELECT @id, company_id, party_id, 'SUPPLIER_INVOICE', si_id, doc_date, due_date, @amount, @amount, 1, 'USD', @usd, @usd FROM pur.supplier_invoice WHERE si_id = @s
            """,
            cancellationToken,
            ("id", apDocId),
            ("amount", payable),
            ("usd", totalUsd),
            ("s", header.Id)).ConfigureAwait(false);
        await BillOrderAsync(context, header, lines, eventId, +1, "Procurement.PostSupplierInvoice", cancellationToken).ConfigureAwait(false);
        await FixedAssetCards.CreateForInvoiceAsync(context, header.Id, eventId, cancellationToken).ConfigureAwait(false); // E-AF-3
        var journal = await engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            supplierInvoiceId = header.Id,
            accountingStatus = "POSTED",
            apDocumentId = apDocId,
            payable = Text(payable),
            payableUsd = Text(totalUsd),
            journals = new[] { journal.JournalId },
            version,
        });
    }

    /// <summary>E-GAS-04-6: the exact reversal of the P-37 journal while the AP document has no applications; its NCF is free again.</summary>
    public static async Task<string> ReverseAsync(CommandContext context, InvoiceHeader header, string reason, PostingEngine engine, string commandType, CancellationToken cancellationToken)
    {
        // E-USD1-04-7: an invoice in a live import settlement is reversed only after the settlement.
        await Imports.ImportSettlements.RequireNotSettledAsync(context, Imports.ImportSettlements.GoodsKind, header.Id, cancellationToken).ConfigureAwait(false);
        await Imports.ImportSettlements.RequireNotSettledAsync(context, Imports.ImportSettlements.ExpenseKind, header.Id, cancellationToken).ConfigureAwait(false);
        var (apDocId, open, postingEventId) = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.ap_doc_id, a.open_amount = a.original_amount, si.posting_event_id
            FROM fin.ap_document a JOIN pur.supplier_invoice si ON si.si_id = a.source_doc_id
            WHERE a.source_doc_id = @s
            FOR UPDATE OF a
            """,
            r => (Id: r.GetGuid(0), Open: r.GetBoolean(1), Event: r.GetGuid(2)),
            cancellationToken,
            ("s", header.Id)).ConfigureAwait(false)).Single();
        if (!open)
        {
            throw new DomainException(ProcurementErrors.ApNotOpen, "The AP document has applications; it can no longer be reversed.");
        }

        var journalId = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT journal_id FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'AUTO'",
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", postingEventId)).ConfigureAwait(false)).Single();
        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var plan = await engine.PrepareReversalAsync(context, journalId, businessDate, cancellationToken).ConfigureAwait(false);

        var version = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierInvoiceReversed", 1, SupplierInvoiceStore.Aggregate, header.Id, version,
                JsonSerializer.Serialize(new { siId = header.Id, apDocId, reversedJournalId = journalId, reason, docClass = SupplierInvoiceClasses.Expense }),
                Publish: true, OccurredAt: occurredAt, BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(SupplierInvoiceStore.Aggregate, header.Id, "DOCUMENT", header.Status, SupplierInvoiceStatus.Reversed, commandType, eventId, cancellationToken, reason)
            .ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE pur.supplier_invoice SET document_status = 'REVERSED', accounting_status = 'REVERSED', version = @v WHERE si_id = @s",
            cancellationToken, ("v", version), ("s", header.Id)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.ap_document SET open_amount = 0, open_amount_fc = CASE WHEN open_amount_fc IS NULL THEN NULL ELSE 0 END, version = version + 1 WHERE ap_doc_id = @a",
            cancellationToken,
            ("a", apDocId)).ConfigureAwait(false);
        var lines = await LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        await OrderLinesAsync(context, lines, cancellationToken).ConfigureAwait(false);
        await BillOrderAsync(context, header, lines, eventId, -1, commandType, cancellationToken).ConfigureAwait(false);
        await FixedAssetCards.CancelForInvoiceAsync(context, header.Id, eventId, commandType, reason, cancellationToken).ConfigureAwait(false); // E-AF1-02-4
        var reversal = await engine.WriteReversalAsync(context, plan, eventId, occurredAt, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status = SupplierInvoiceStatus.Reversed, journals = new[] { reversal.JournalId }, version });
    }

    /// <summary>
    /// E-GAS-05-3: each line against its order line — the quantity still to bill (never exceeded, never approvable) and the order's
    /// price within the PURCHASING tolerances; a price outside them is a MATCH_EXCEPTION the Controller approves.
    /// </summary>
    private static async Task<string> MatchToOrderAsync(
        CommandContext context, InvoiceHeader header, ResolvedPolicy policy, IReadOnlyList<Line> lines, string commandType, CancellationToken cancellationToken)
    {
        var pricePct = policy.Decimal(PolicyParameters.MatchPriceTolerancePct);
        var amountAbs = policy.Decimal(PolicyParameters.MatchAmountToleranceAbs);
        var order = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT po_line_id, qty_ordered - qty_invoiced, unit_price FROM pur.purchase_order_line WHERE po_line_id = ANY(@ids)",
            r => (Id: r.GetGuid(0), Open: r.GetDecimal(1), Price: r.GetDecimal(2)),
            cancellationToken,
            ("ids", lines.Select(l => l.PoLineId!.Value).ToArray())).ConfigureAwait(false)).ToDictionary(o => o.Id);
        var results = new List<(Guid Line, decimal Available, decimal QtyDiff, decimal PriceDiff, decimal AmountDiff, bool Exceeds, bool Within)>();
        foreach (var line in lines)
        {
            var po = order[line.PoLineId!.Value];
            var qtyDiff = line.Quantity - po.Open;
            // E-USD1-03-2: a USD order's prices are in USD; the difference in amount is valued in pesos at the invoice's rate.
            var priceDiff = (line.UnitPriceFc ?? line.UnitPrice) - po.Price;
            var amountDiff = decimal.Round(line.Quantity * priceDiff * (header.ExchangeRate ?? 1m), 2, MidpointRounding.AwayFromZero);
            var exceeds = qtyDiff > 0m;
            var priceOk = Math.Abs(priceDiff) <= po.Price * pricePct || Math.Abs(amountDiff) <= amountAbs;
            results.Add((line.Id, po.Open, qtyDiff, priceDiff, amountDiff, exceeds, !exceeds && priceOk));
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.match_result (si_line_id, company_id, qty_available_to_invoice, qty_diff, price_diff, amount_diff, qty_exceeds, within_tolerance, policy_version_id, evaluated_at)
                VALUES (@line, @c, @available, @qtyDiff, @priceDiff, @amountDiff, @exceeds, @within, @policy, @at)
                ON CONFLICT (si_line_id) DO UPDATE SET
                  qty_available_to_invoice = EXCLUDED.qty_available_to_invoice, qty_diff = EXCLUDED.qty_diff, price_diff = EXCLUDED.price_diff,
                  amount_diff = EXCLUDED.amount_diff, qty_exceeds = EXCLUDED.qty_exceeds, within_tolerance = EXCLUDED.within_tolerance,
                  policy_version_id = EXCLUDED.policy_version_id, evaluated_at = EXCLUDED.evaluated_at
                """,
                cancellationToken,
                ("line", line.Id),
                ("c", context.CompanyId),
                ("available", po.Open),
                ("qtyDiff", qtyDiff),
                ("priceDiff", priceDiff),
                ("amountDiff", amountDiff),
                ("exceeds", exceeds),
                ("within", !exceeds && priceOk),
                ("policy", policy.PolicyVersionId),
                ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        }

        var matched = results.All(r => r.Within);
        var status = matched ? SupplierInvoiceStatus.Matched : SupplierInvoiceStatus.MatchException;
        var version = await SupplierInvoiceStore.TransitionAsync(
            context,
            header,
            status,
            commandType,
            matched ? "SupplierInvoiceMatched" : "MatchExceptionRaised",
            new
            {
                siId = header.Id,
                status,
                docClass = SupplierInvoiceClasses.Expense,
                policyVersionId = policy.PolicyVersionId,
                lines = results.Select(r => new
                {
                    siLineId = r.Line,
                    qtyAvailable = r.Available.ToString(CultureInfo.InvariantCulture),
                    qtyDiff = r.QtyDiff.ToString(CultureInfo.InvariantCulture),
                    priceDiff = r.PriceDiff.ToString(CultureInfo.InvariantCulture),
                    amountDiff = Text(r.AmountDiff),
                    qtyExceeds = r.Exceeds,
                    withinTolerance = r.Within,
                }),
            },
            publish: !matched,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status, qtyExceeds = results.Any(r => r.Exceeds), version });
    }

    /// <summary>Locks the order lines an invoice bills (by id) and returns, for each invoice line with one, what the order has still to bill.</summary>
    private static async Task<List<(Line Line, decimal Open)>> OrderLinesAsync(CommandContext context, IReadOnlyList<Line> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Where(l => l.PoLineId is not null).Select(l => l.PoLineId!.Value).Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        // Lock order: the order (N3) before its lines (N4), as every purchase order command takes them.
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT 1 FROM pur.purchase_order WHERE po_id IN (SELECT po_id FROM pur.purchase_order_line WHERE po_line_id = ANY(@ids)) ORDER BY po_id FOR UPDATE",
            cancellationToken,
            ("ids", ids)).ConfigureAwait(false);
        var open = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT po_line_id, qty_ordered - qty_invoiced FROM pur.purchase_order_line WHERE po_line_id = ANY(@ids) ORDER BY po_line_id FOR UPDATE",
            r => (Id: r.GetGuid(0), Open: r.GetDecimal(1)),
            cancellationToken,
            ("ids", ids)).ConfigureAwait(false)).ToDictionary(o => o.Id, o => o.Open);
        return [.. lines.Where(l => l.PoLineId is not null).Select(l => (l, open[l.PoLineId!.Value]))];
    }

    /// <summary>
    /// E-GAS-05-4: what an invoice bills of its order (<paramref name="sign"/> +1 when posted, −1 when reversed), its BILLS links, and
    /// the order's status — CLOSED once billed in full, APPROVED again when a reversal leaves something to bill.
    /// </summary>
    private static async Task BillOrderAsync(CommandContext context, InvoiceHeader header, IReadOnlyList<Line> lines, Guid eventId, int sign, string commandType, CancellationToken cancellationToken)
    {
        var billed = lines.Where(l => l.PoLineId is not null).ToList();
        if (billed.Count == 0)
        {
            return;
        }

        foreach (var line in billed)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE pur.purchase_order_line SET qty_invoiced = qty_invoiced + @q, version = version + 1 WHERE po_line_id = @l",
                cancellationToken, ("q", sign * line.Quantity), ("l", line.PoLineId)).ConfigureAwait(false);
            if (sign > 0)
            {
                await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    """
                    INSERT INTO core.document_link (link_id, company_id, from_type, from_id, from_line_id, to_type, to_id, to_line_id, link_type, qty, amount, event_id)
                    SELECT @id, @c, 'SupplierInvoice', @si, @sil, 'PurchaseOrder', po_id, po_line_id, 'BILLS', @qty, @amount, @event FROM pur.purchase_order_line WHERE po_line_id = @pol
                    """,
                    cancellationToken,
                    ("id", context.Ids.NewId()),
                    ("c", context.CompanyId),
                    ("si", header.Id),
                    ("sil", line.Id),
                    ("pol", line.PoLineId),
                    ("qty", line.Quantity),
                    ("amount", line.Net),
                    ("event", eventId)).ConfigureAwait(false);
            }
        }

        var poId = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT po_id FROM pur.purchase_order_line WHERE po_line_id = @l", r => r.GetGuid(0), cancellationToken, ("l", billed[0].PoLineId))
            .ConfigureAwait(false)).Single();
        var po = await PurchaseOrderStore.LockAsync(context, poId, header.PlantId!.Value, null, cancellationToken).ConfigureAwait(false);
        var complete = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT bool_and(qty_invoiced >= qty_ordered) FROM pur.purchase_order_line WHERE po_id = @p", r => r.GetBoolean(0), cancellationToken, ("p", poId))
            .ConfigureAwait(false)).Single();
        if (sign > 0 && complete && po.Status == PurchaseOrderStatus.Approved)
        {
            await PurchaseOrderStore.TransitionAsync(
                context, po, PurchaseOrderStatus.Closed, commandType, "PurchaseOrderClosed", new { poId, billedBy = header.Id }, publish: true, cancellationToken).ConfigureAwait(false);
        }
        else if (sign < 0 && !complete && po.Status == PurchaseOrderStatus.Closed)
        {
            await PurchaseOrderStore.TransitionAsync(
                context, po, PurchaseOrderStatus.Approved, commandType, "PurchaseOrderReopened", new { poId, reversedInvoice = header.Id }, publish: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An ACTIVE supplier; every category ACTIVE and every tax type in force on <paramref name="date"/> (E-GAS-02-4, E-GAS-02-7). Returns the
    /// documents' currency: USD for a foreign supplier, whose lines carry no tax type; DOP otherwise, every line with one (E-USD1-03-3).
    /// </summary>
    public static async Task<string> RequireSupplierAndTypesAsync(
        CommandContext context, Guid partyId, DateOnly date, IEnumerable<Guid> categoryIds, IReadOnlyCollection<Guid?> taxTypeIds, CancellationToken cancellationToken)
    {
        var categories = categoryIds.Distinct().ToArray();
        var types = taxTypeIds.OfType<Guid>().Distinct().ToArray();
        var found = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce((SELECT status = 'ACTIVE' AND is_supplier FROM md.party WHERE company_id = @c AND party_id = @p), false),
                   coalesce((SELECT party_kind = 'FOREIGN' FROM md.party WHERE company_id = @c AND party_id = @p), false),
                   (SELECT count(*) FROM pur.expense_category WHERE company_id = @c AND expense_category_id = ANY(@cats) AND status = 'ACTIVE'),
                   (SELECT count(*) FROM tax.fiscal_rule r WHERE r.company_id = @c AND r.rule_id = ANY(@types) AND r.rule_kind = 'PURCHASE_TAX_TYPE'
                      AND EXISTS (SELECT 1 FROM tax.fiscal_rule_version v WHERE v.rule_id = r.rule_id AND v.status = 'ACTIVE' AND v.effective_from <= @d
                                    AND (v.effective_to IS NULL OR v.effective_to > @d)))
            """,
            r => (Supplier: r.GetBoolean(0), Foreign: r.GetBoolean(1), Categories: r.GetInt64(2), Types: r.GetInt64(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", partyId),
            ("cats", categories),
            ("types", types),
            ("d", date)).ConfigureAwait(false)).Single();
        if (!found.Supplier)
        {
            throw new DomainException(ProcurementErrors.SupplierNotActive, "The supplier does not exist or is not ACTIVE.");
        }

        if (found.Categories != categories.Length)
        {
            throw new DomainException(ExpenseErrors.CategoryNotFound, "Every line needs an ACTIVE expense category.");
        }

        if (found.Foreign ? types.Length > 0 : taxTypeIds.Any(t => t is null))
        {
            throw new DomainException(
                ExpenseErrors.TaxTypeCurrency,
                found.Foreign ? "A foreign supplier's lines are in USD without tax type (E-USD1-03-3)." : "Every line needs a tax type (E-GAS-02-4).");
        }

        if (found.Types != types.Length)
        {
            throw new DomainException(TaxErrors.FiscalGateClosed, $"Every line needs a tax type in force on {date:yyyy-MM-dd} (E-GAS-02-7).");
        }

        return found.Foreign ? Usd : "DOP";
    }

    /// <summary>
    /// E-GAS-05-2: an invoice cites one APPROVED expense order of its supplier and plant, or none. With an order every line names a
    /// different line of it and carries that line's category and tax type; without one, no line names an order line.
    /// </summary>
    public static async Task RequireOrderAsync(CommandContext context, RegisterExpenseInvoice command, CancellationToken cancellationToken)
    {
        if (command.PurchaseOrderId is not { } poId)
        {
            if (command.Lines.Any(l => l.PurchaseOrderLineId is not null))
            {
                throw new DomainException(ProcurementErrors.LineNotFound, "A line names an order line, but the invoice cites no order.");
            }

            return;
        }

        var order = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT party_id, plant_id, status::text, doc_class FROM pur.purchase_order WHERE company_id = @c AND po_id = @p",
            r => (Party: r.GetGuid(0), Plant: r.GetGuid(1), Status: r.GetString(2), Class: r.GetString(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", poId)).ConfigureAwait(false)).SingleOrDefault();
        if (order.Class != SupplierInvoiceClasses.Expense || order.Party != command.PartyId)
        {
            throw new DomainException(ProcurementErrors.NotFound, "The order does not exist, is not an expense order or is another supplier's.");
        }

        if (order.Status != PurchaseOrderStatus.Approved)
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The order is {order.Status}: an invoice bills an APPROVED expense order.");
        }

        if (order.Plant != command.PlantId)
        {
            throw new DomainException(ProcurementErrors.PlantMismatch, "The invoice's plant is the order's (E-GAS-01-8).");
        }

        var lines = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT po_line_id, expense_category_id, tax_rule_id FROM pur.purchase_order_line WHERE po_id = @p",
            r => (Id: r.GetGuid(0), Category: r.GetGuid(1), Tax: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2)),
            cancellationToken,
            ("p", poId)).ConfigureAwait(false)).ToDictionary(l => l.Id);
        if (command.Lines.Any(l => l.PurchaseOrderLineId is not { } id || !lines.ContainsKey(id))
            || command.Lines.Select(l => l.PurchaseOrderLineId).Distinct().Count() != command.Lines.Count)
        {
            throw new DomainException(ProcurementErrors.DuplicateLine, "With an order, every line names a different line of that order.");
        }

        if (command.Lines.Any(l => lines[l.PurchaseOrderLineId!.Value] is var o && (o.Category != l.ExpenseCategoryId || o.Tax != l.TaxTypeId)))
        {
            throw new DomainException(ExpenseErrors.CategoryInvalid, "A line billing an order line carries that line's category and tax type (E-GAS-05-2).");
        }
    }

    public static (string Description, decimal Quantity, decimal UnitPrice, decimal Net) ValidLine(ExpenseLineInput line)
    {
        var description = (line.Description ?? string.Empty).Trim();
        if (description.Length is 0 or > 200)
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "Each line says what was bought in 1 to 200 characters.");
        }

        if (line.Quantity <= 0m || line.Quantity > MaxQuantity || decimal.Round(line.Quantity, 6) != line.Quantity)
        {
            throw new DomainException(ProcurementErrors.QuantityInvalid, "Each quantity is greater than zero, with at most 6 decimals.");
        }

        if (line.UnitPrice <= 0m || line.UnitPrice > MaxUnitPrice || decimal.Round(line.UnitPrice, 6) != line.UnitPrice)
        {
            throw new DomainException(ProcurementErrors.PriceInvalid, "Each price is greater than zero, with at most 6 decimals.");
        }

        return (description, line.Quantity, line.UnitPrice, decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>An amount as the API writes it: 2 decimals (ADR-015).</summary>
    public static string Text(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}

[RequiresPermission("supplier_invoice:register")]
public sealed class RegisterExpenseInvoiceHandler : ICommandHandler<RegisterExpenseInvoice>
{
    public string CommandType => "Procurement.RegisterExpenseInvoice";

    public async Task<string> HandleAsync(RegisterExpenseInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var foreign = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT party_kind = 'FOREIGN' FROM md.party WHERE company_id = @c AND party_id = @p", r => r.GetBoolean(0), cancellationToken,
            ("c", context.CompanyId), ("p", command.PartyId)).ConfigureAwait(false)).SingleOrDefault();
        var fiscalNumber = (command.SupplierFiscalNumber ?? string.Empty).Trim();
        if (foreign && fiscalNumber.Length is 0 or > 40)
        {
            throw new DomainException(ExpenseErrors.ForeignNumberInvalid, "The foreign supplier's invoice number has 1 to 40 characters (E-USD1-03-3).");
        }

        fiscalNumber = foreign ? fiscalNumber : fiscalNumber.ToUpperInvariant();
        if (!foreign && !SupplierInvoiceStore.FiscalNumberFormat().IsMatch(fiscalNumber))
        {
            throw new DomainException(ProcurementErrors.FiscalNumberInvalid, "The supplier fiscal number must be an NCF (B + 10 digits) or an e-NCF (E + 12 digits).");
        }

        if (command.PrintedTotal is { } printed && (printed <= 0m || decimal.Round(printed, 2) != printed))
        {
            throw new DomainException(ProcurementErrors.PrintedTotalInvalid, "The printed total must be greater than zero with at most 2 decimals.");
        }

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        if (command.DocDate > today || command.DueDate < command.DocDate)
        {
            throw new DomainException(ProcurementErrors.DateInvalid, "The invoice date cannot be in the future and the due date cannot precede it.");
        }

        if (command.Lines is not { Count: > 0 and <= 200 })
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "An expense invoice has 1 to 200 lines.");
        }

        var lines = command.Lines.Select(ExpenseInvoices.ValidLine).ToList();
        var checks = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT EXISTS (SELECT 1 FROM md.plant WHERE company_id = @c AND plant_id = @plant),
                   EXISTS (SELECT 1 FROM pur.supplier_invoice WHERE company_id = @c AND party_id = @p AND supplier_fiscal_number = @n AND document_status NOT IN ('VOIDED', 'REVERSED'))
            """,
            r => (Plant: r.GetBoolean(0), Used: r.GetBoolean(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", command.PartyId),
            ("plant", command.PlantId),
            ("n", fiscalNumber)).ConfigureAwait(false)).Single();
        var currency = await ExpenseInvoices.RequireSupplierAndTypesAsync(
            context, command.PartyId, command.DocDate, command.Lines.Select(l => l.ExpenseCategoryId), [.. command.Lines.Select(l => l.TaxTypeId)], cancellationToken).ConfigureAwait(false);
        if (!checks.Plant)
        {
            throw new DomainException(ProcurementErrors.PlantMismatch, "The plant does not exist (E-GAS-01-8).");
        }

        if (checks.Used)
        {
            throw new DomainException(ProcurementErrors.FiscalNumberUsed, $"Fiscal number {fiscalNumber} is already registered for this supplier.");
        }

        await ExpenseInvoices.RequireOrderAsync(context, command, cancellationToken).ConfigureAwait(false);

        // E-USD1-03-4: a USD invoice takes the rate of its date; each line's peso net is its USD net at that rate, and the rounding cent
        // goes to the largest line so that the lines add up to the peso total.
        decimal? rate = currency == ExpenseInvoices.Usd
            ? (await ExchangeRateBook.ForDateAsync(context.Connection, context.Transaction, context.CompanyId, currency, command.DocDate, cancellationToken).ConfigureAwait(false)).Rate
            : null;
        var totalUsd = rate is null ? (decimal?)null : lines.Sum(l => l.Net);
        var pesos = lines.Select(l => rate is { } r ? ExchangeRateBook.ToPesos(l.Net, r) : l.Net).ToList();
        if (rate is { } x)
        {
            var largest = lines.Select((l, i) => (l.Net, i)).OrderByDescending(t => t.Net).ThenBy(t => t.i).First().i;
            pesos[largest] += ExchangeRateBook.ToPesos(totalUsd!.Value, x) - pesos.Sum();
        }

        var document = await SupplierDocuments.SupplierDocumentRegistration.CheckAsync(context, command.SupplierDocumentId, command.PartyId, fiscalNumber, cancellationToken)
            .ConfigureAwait(false); // E-OCR1-03-4
        var total = pesos.Sum();
        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var siId = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierInvoiceRegistered",
                1,
                SupplierInvoiceStore.Aggregate,
                siId,
                1,
                JsonSerializer.Serialize(new
                {
                    siId,
                    docClass = SupplierInvoiceClasses.Expense,
                    partyId = command.PartyId,
                    plantId = command.PlantId,
                    supplierFiscalNumber = fiscalNumber,
                    docDate = command.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    dueDate = command.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    totalAmount = ExpenseInvoices.Text(total),
                    printedTotal = command.PrintedTotal?.ToString(CultureInfo.InvariantCulture),
                    currency,
                    exchangeRate = rate?.ToString("0.0000", CultureInfo.InvariantCulture),
                    totalAmountUsd = totalUsd is { } usd ? ExpenseInvoices.Text(usd) : null,
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(SupplierInvoiceStore.Aggregate, siId, "DOCUMENT", null, SupplierInvoiceStatus.Draft, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version,
                  printed_total, doc_class, plant_id, currency, exchange_rate, total_amount_fc)
                VALUES (@id, @c, @party, @ncf, @doc, @due, 'DRAFT', 'NOT_POSTED', @total, @by, 1, @printed, 'EXPENSE', @plant, @currency, @rate, @usd)
                """,
                cancellationToken,
                ("id", siId),
                ("c", context.CompanyId),
                ("party", command.PartyId),
                ("ncf", fiscalNumber),
                ("doc", command.DocDate),
                ("due", command.DueDate),
                ("total", total),
                ("by", creator),
                ("printed", command.PrintedTotal),
                ("plant", command.PlantId),
                ("currency", currency),
                ("rate", rate),
                ("usd", totalUsd)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ProcurementErrors.FiscalNumberUsed, $"Fiscal number {fiscalNumber} is already registered for this supplier.");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.supplier_invoice_line (si_line_id, company_id, si_id, line_no, line_kind, po_line_id, qty, unit_price, net_amount, description, expense_category_id, tax_rule_id,
                  unit_price_fc, net_amount_fc)
                VALUES (@id, @c, @si, @no, 'EXPENSE', @pol, @qty, @price, @net, @description, @category, @tax, @price_fc, @net_fc)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("si", siId),
                ("no", i + 1),
                ("qty", lines[i].Quantity),
                ("price", rate is { } r ? decimal.Round(lines[i].UnitPrice * r, 6, MidpointRounding.AwayFromZero) : lines[i].UnitPrice),
                ("net", pesos[i]),
                ("price_fc", rate is null ? (decimal?)null : lines[i].UnitPrice),
                ("net_fc", rate is null ? (decimal?)null : lines[i].Net),
                ("description", lines[i].Description),
                ("category", command.Lines[i].ExpenseCategoryId),
                ("tax", command.Lines[i].TaxTypeId),
                ("pol", command.Lines[i].PurchaseOrderLineId)).ConfigureAwait(false);
        }

        await SupplierDocuments.SupplierDocumentRegistration.LinkAsync(context, document, siId, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            supplierInvoiceId = siId,
            status = SupplierInvoiceStatus.Draft,
            totalAmount = ExpenseInvoices.Text(total),
            currency,
            exchangeRate = rate?.ToString("0.0000", CultureInfo.InvariantCulture),
            totalAmountUsd = totalUsd is { } usdTotal ? ExpenseInvoices.Text(usdTotal) : null,
            version = 1,
        });
    }
}
