using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Finance.Posting;
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
    public const string ApprovalThreshold = "expense_invoice_approval_threshold";
    private const decimal MaxQuantity = 999_999_999_999.999999m; // type-limit: numeric(18,6)
    private const decimal MaxUnitPrice = 9_999_999_999_999.999999m; // type-limit: numeric(19,6)

    public sealed record Line(Guid Id, int LineNo, string Description, decimal Quantity, decimal UnitPrice, decimal Net, Guid TaxTypeId, Guid CategoryId, Guid AccountId, string Category, string Scope);

    public static async Task<IReadOnlyList<Line>> LinesAsync(CommandContext context, Guid siId, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.si_line_id, l.line_no, l.description, l.qty, l.unit_price, l.net_amount, l.tax_rule_id, c.expense_category_id, c.account_id, c.code,
                   CASE c.line_class WHEN 'SERVICE' THEN 'EXPENSE_SERVICE' ELSE 'EXPENSE_GOODS' END
            FROM pur.supplier_invoice_line l JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
            WHERE l.si_id = @s ORDER BY l.line_no
            """,
            r => new Line(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetGuid(6), r.GetGuid(7), r.GetGuid(8), r.GetString(9), r.GetString(10)),
            cancellationToken,
            ("s", siId)).ConfigureAwait(false);

    private static List<TaxLineInput> TaxLines(IEnumerable<Line> lines) => [.. lines.Select(l => new TaxLineInput(l.Id, null, l.Net, l.TaxTypeId, l.Scope))];

    /// <summary>
    /// E-GAS-04-2/3: the invoice's total — the net plus every tax of its lines' types in force on its date — against the policy's
    /// approval amount: below it the invoice is MATCHED; from it, a MATCH_EXCEPTION the Controller approves.
    /// </summary>
    public static async Task<string> MatchAsync(CommandContext context, InvoiceHeader header, ResolvedPolicy policy, string commandType, CancellationToken cancellationToken)
    {
        var threshold = policy.Decimal(ApprovalThreshold);
        var lines = await LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        var estimate = await TaxEngine.EstimateItbisAsync(context.Connection, context.Transaction, context.CompanyId, header.DocDate, false, TaxLines(lines), cancellationToken).ConfigureAwait(false);
        if (estimate.Total is not { } taxes)
        {
            throw new DomainException(estimate.UnavailableCode ?? TaxErrors.FiscalGateClosed, estimate.UnavailableReason ?? "The taxes of the invoice cannot be determined on its date.");
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
        var determination = await tax.DetermineAsync(
            context, new TaxRequest("SupplierInvoice", header.Id, header.DocDate, header.PartyId, TaxLines(lines)), cancellationToken).ConfigureAwait(false);
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

    /// <summary>E-GAS-04-6: the exact reversal of the P-37 journal while the AP document has no applications; its NCF is free again.</summary>
    public static async Task<string> ReverseAsync(CommandContext context, InvoiceHeader header, string reason, PostingEngine engine, string commandType, CancellationToken cancellationToken)
    {
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
            context.Connection, context.Transaction, "UPDATE fin.ap_document SET open_amount = 0, version = version + 1 WHERE ap_doc_id = @a", cancellationToken, ("a", apDocId)).ConfigureAwait(false);
        var reversal = await engine.WriteReversalAsync(context, plan, eventId, occurredAt, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status = SupplierInvoiceStatus.Reversed, journals = new[] { reversal.JournalId }, version });
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
        var fiscalNumber = (command.SupplierFiscalNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (!SupplierInvoiceStore.FiscalNumberFormat().IsMatch(fiscalNumber))
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
            SELECT (SELECT status = 'ACTIVE' AND is_supplier FROM md.party WHERE company_id = @c AND party_id = @p),
                   EXISTS (SELECT 1 FROM md.plant WHERE company_id = @c AND plant_id = @plant),
                   EXISTS (SELECT 1 FROM pur.supplier_invoice WHERE company_id = @c AND party_id = @p AND supplier_fiscal_number = @n AND document_status NOT IN ('VOIDED', 'REVERSED'))
            """,
            r => (Supplier: !r.IsDBNull(0) && r.GetBoolean(0), Plant: r.GetBoolean(1), Used: r.GetBoolean(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", command.PartyId),
            ("plant", command.PlantId),
            ("n", fiscalNumber)).ConfigureAwait(false)).Single();
        if (!checks.Supplier)
        {
            throw new DomainException(ProcurementErrors.SupplierNotActive, "The supplier does not exist or is not ACTIVE.");
        }

        if (!checks.Plant)
        {
            throw new DomainException(ProcurementErrors.PlantMismatch, "The plant does not exist (E-GAS-01-8).");
        }

        if (checks.Used)
        {
            throw new DomainException(ProcurementErrors.FiscalNumberUsed, $"Fiscal number {fiscalNumber} is already registered for this supplier.");
        }

        // An ACTIVE category and a tax type in force on the invoice's date for every line (E-GAS-02-4, E-GAS-02-7).
        var categories = command.Lines.Select(l => l.ExpenseCategoryId).Distinct().ToArray();
        var types = command.Lines.Select(l => l.TaxTypeId).Distinct().ToArray();
        var found = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT (SELECT count(*) FROM pur.expense_category WHERE company_id = @c AND expense_category_id = ANY(@cats) AND status = 'ACTIVE'),
                   (SELECT count(*) FROM tax.fiscal_rule r WHERE r.company_id = @c AND r.rule_id = ANY(@types) AND r.rule_kind = 'PURCHASE_TAX_TYPE'
                      AND EXISTS (SELECT 1 FROM tax.fiscal_rule_version v WHERE v.rule_id = r.rule_id AND v.status = 'ACTIVE' AND v.effective_from <= @d
                                    AND (v.effective_to IS NULL OR v.effective_to > @d)))
            """,
            r => (Categories: r.GetInt64(0), Types: r.GetInt64(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("cats", categories),
            ("types", types),
            ("d", command.DocDate)).ConfigureAwait(false)).Single();
        if (found.Categories != categories.Length)
        {
            throw new DomainException(ExpenseErrors.CategoryNotFound, "Every line needs an ACTIVE expense category.");
        }

        if (found.Types != types.Length)
        {
            throw new DomainException(TaxErrors.FiscalGateClosed, $"Every line needs a tax type in force on {command.DocDate:yyyy-MM-dd} (E-GAS-02-7).");
        }

        var total = lines.Sum(l => l.Net);
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
                  printed_total, doc_class, plant_id)
                VALUES (@id, @c, @party, @ncf, @doc, @due, 'DRAFT', 'NOT_POSTED', @total, @by, 1, @printed, 'EXPENSE', @plant)
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
                ("plant", command.PlantId)).ConfigureAwait(false);
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
                INSERT INTO pur.supplier_invoice_line (si_line_id, company_id, si_id, line_no, line_kind, po_line_id, qty, unit_price, net_amount, description, expense_category_id, tax_rule_id)
                VALUES (@id, @c, @si, @no, 'EXPENSE', NULL, @qty, @price, @net, @description, @category, @tax)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("si", siId),
                ("no", i + 1),
                ("qty", lines[i].Quantity),
                ("price", lines[i].UnitPrice),
                ("net", lines[i].Net),
                ("description", lines[i].Description),
                ("category", command.Lines[i].ExpenseCategoryId),
                ("tax", command.Lines[i].TaxTypeId)).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { supplierInvoiceId = siId, status = SupplierInvoiceStatus.Draft, totalAmount = ExpenseInvoices.Text(total), version = 1 });
    }
}
