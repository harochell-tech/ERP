using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Proformas;
using Rochell.Sales.Receipts;
using Rochell.Tax;
using Rochell.Tax.Authorizations;

namespace Rochell.Sales.Invoices;

public static class InvoiceErrors
{
    public const string NotBillable = "DELIVERY_LINE_NOT_BILLABLE";
    public const string QuantityExceedsDelivered = "INVOICE_EXCEEDS_DELIVERED";
    public const string EcfTypeInvalid = "ECF_TYPE_INVALID";
    public const string EncfInvalid = "ENCF_INVALID";
    public const string EncfDuplicate = "ENCF_DUPLICATE";
    public const string FiscalDocumentMismatch = "FISCAL_DOCUMENT_MISMATCH";
    public const string NotVoidable = "INVOICE_NOT_VOIDABLE";
    public const string TermsMissing = "CUSTOMER_TERMS_REQUIRED";

    /// <summary>E-CF1-03-1: an invoice of the final consumer comes from one order — each order has its buyer.</summary>
    public const string OneCashOrder = "CASH_INVOICE_ONE_ORDER";
}

internal static class Invoicing
{
    public const string Aggregate = "Invoice";
    public const string RuleCode = "P-18";

    public sealed record Row(
        Guid PartyId, string InvoiceNo, string Commercial, string Accounting, string Fiscal, string EcfType, decimal Net, decimal? Tax, decimal? Total, Guid? ArDocId, Guid? PostingEventId,
        Guid CreatedBy, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid invoiceId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT party_id, invoice_no, commercial_status, accounting_status, fiscal_status, ecf_type, net_total::numeric(19,2), tax_total::numeric(19,2), total::numeric(19,2),
                   ar_doc_id, posting_event_id, created_by, version
            FROM sal.invoice WHERE company_id = @c AND invoice_id = @i FOR UPDATE
            """,
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetDecimal(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8), r.NullableGuid(9), r.NullableGuid(10), r.GetGuid(11), r.GetInt64(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("i", invoiceId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The invoice does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(SalesErrors.VersionConflict, $"The invoice changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    public sealed record Line(Guid InvoiceLineId, Guid DeliveryLineId, Guid OrderLineId, Guid ItemId, decimal Quantity, decimal Net, string DeliveryNo, string Uom);

    public static Task<List<Line>> LinesAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT il.invoice_line_id, il.delivery_line_id, dl.sales_order_line_id, il.item_id, il.quantity, il.net_amount::numeric(19,2), d.delivery_no, il.uom
            FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id JOIN log.delivery d ON d.delivery_id = dl.delivery_id
            WHERE il.invoice_id = @i ORDER BY il.delivery_line_id
            """,
            r => new Line(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetDecimal(4), r.GetDecimal(5), r.GetString(6), r.GetString(7)),
            cancellationToken,
            ("i", invoiceId));

    public static string DefaultEcfType(string? rnc) => rnc?.Length == 9 ? "31" : "32";

    /// <summary>E-FIS1-01-7: the e-CF type of an invoice under a fiscal authorization (Regímenes Especiales).</summary>
    public const string ExemptEcfType = "44";

    public static Task<Guid?> AuthorizationAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
        => SalesSql.ScalarAsync<Guid?>(context, "SELECT fiscal_authorization_id FROM sal.invoice WHERE invoice_id = @i", cancellationToken, ("i", invoiceId));

    public static List<CoveredLine> Covered(IEnumerable<Line> lines) => [.. lines.Select(l => new CoveredLine(l.InvoiceLineId, l.ItemId, l.Uom, l.Quantity, l.Net))];

    public static string M(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The DRAFT invoice of delivered lines, shared by the delivery path (VS#3) and the proforma path (E-FIS1b-01-6).</summary>
/// <summary>
/// E-CF1-6, E-CF1-01-7: who the e-CF of an invoice names as its receiver. A customer's invoice names the customer's RNC or cédula;
/// the final consumer's names the buyer of its order — a cédula or RNC, a passport, or nobody.
/// </summary>
internal static class FiscalReceiver
{
    public sealed record Expected(string Rnc, string? Passport);

    public static async Task<Expected> ExpectedAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
        => (await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               """
               SELECT CASE WHEN p.party_kind = 'CONSUMER' THEN CASE WHEN i.buyer_id_kind IN ('CEDULA', 'RNC') THEN i.buyer_id ELSE '' END ELSE coalesce(p.rnc, '') END,
                      CASE WHEN p.party_kind = 'CONSUMER' AND i.buyer_id_kind = 'PASAPORTE' THEN i.buyer_id END
               FROM sal.invoice i JOIN md.party p ON p.party_id = i.party_id WHERE i.invoice_id = @i
               """,
               r => new Expected(r.GetString(0), r.NullableString(1)),
               cancellationToken,
               ("i", invoiceId)).ConfigureAwait(false))!;

    public static (string Rnc, string? Passport) Given(string? rnc, string? passport)
        => (new string([.. (rnc ?? string.Empty).Where(char.IsAsciiDigit)]), string.IsNullOrWhiteSpace(passport) ? null : passport.Trim().ToUpperInvariant());

    public static IEnumerable<string> Differences(Expected expected, string rnc, string? passport)
    {
        if (rnc != expected.Rnc)
        {
            yield return $"receiver {(rnc.Length == 0 ? "(none)" : rnc)} ≠ {(expected.Rnc.Length == 0 ? "(none)" : expected.Rnc)}";
        }

        if (passport != expected.Passport)
        {
            yield return $"receiver passport {passport ?? "(none)"} ≠ {expected.Passport ?? "(none)"}";
        }
    }
}

internal static class InvoiceDrafts
{
    private sealed record Billable(Guid DeliveryLineId, Guid ItemId, string Uom, decimal Remaining, decimal UnitPrice, Guid OrderId);

    private sealed record Buyer(string? Name, string? IdKind, string? Id);

    public static async Task<string> CreateAsync(
        CommandContext context, string commandType, Guid partyId, IReadOnlyList<Guid>? deliveryLineIds, Guid? authorizationId, bool fromProformas, CancellationToken cancellationToken)
    {
        var ids = deliveryLineIds ?? [];
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new DomainException(SalesErrors.LinesRequired, "An invoice has at least one delivery line, each once.");
        }

        var rnc = await SalesSql.ScalarAsync<string>(
            context, "SELECT coalesce(rnc, '') FROM md.party WHERE company_id = @c AND party_id = @p AND is_customer", cancellationToken, ("c", context.CompanyId), ("p", partyId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotCustomer, "The party is not a customer.");
        var billable = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT dl.delivery_line_id, dl.item_id, dl.uom, dl.qty_delivered - dl.qty_invoiced, ol.unit_price, o.sales_order_id
            FROM log.delivery_line dl
            JOIN log.delivery d ON d.delivery_id = dl.delivery_id
            JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id
            WHERE dl.company_id = @c AND dl.delivery_line_id = ANY (@ids) AND o.party_id = @p AND d.status IN ('DELIVERED', 'DELIVERED_WITH_EXCEPTIONS')
            """,
            r => new Billable(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetGuid(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", ids.ToArray()),
            ("p", partyId)).ConfigureAwait(false);
        if (billable.Count != ids.Count || billable.Any(b => b.Remaining <= 0m))
        {
            throw new DomainException(InvoiceErrors.NotBillable, "Each line must be a delivered, not yet fully invoiced delivery line of this customer (E-VS3-05-3).");
        }

        // E-CF1-6, E-CF1-9, E-CF1-03-1: the final consumer's invoice comes from one cash order, carries its buyer and is never exempt.
        Buyer? buyer = null;
        if (await SalesSql.ScalarAsync<string>(context, "SELECT party_kind FROM md.party WHERE party_id = @p", cancellationToken, ("p", partyId)).ConfigureAwait(false) == "CONSUMER")
        {
            if (authorizationId is not null)
            {
                throw new DomainException(InvoiceErrors.EcfTypeInvalid, "A sale to the final consumer has no fiscal authorization: it is an e-CF 32 with ITBIS (E-CF1-9).");
            }

            if (billable.Select(b => b.OrderId).Distinct().Count() != 1)
            {
                throw new DomainException(InvoiceErrors.OneCashOrder, "An invoice of the final consumer takes the deliveries of one order: each order has its buyer (E-CF1-03-1).");
            }

            buyer = await Reading.SingleOrDefaultAsync(
                context.Connection, context.Transaction, "SELECT buyer_name, buyer_id_kind, buyer_id FROM sal.sales_order WHERE sales_order_id = @o",
                r => new Buyer(r.NullableString(0), r.NullableString(1), r.NullableString(2)), cancellationToken, ("o", billable[0].OrderId)).ConfigureAwait(false);
        }

        // E-FIS1b-01-6: a delivery collected on a proforma is invoiced from the proforma, whole.
        if (!fromProformas && await SalesSql.ScalarAsync<bool>(
                context,
                "SELECT EXISTS (SELECT 1 FROM sal.proforma_line l JOIN sal.proforma p ON p.proforma_id = l.proforma_id WHERE l.company_id = @c AND l.delivery_line_id = ANY (@ids) AND p.status <> 'VOIDED')",
                cancellationToken, ("c", context.CompanyId), ("ids", ids.ToArray())).ConfigureAwait(false))
        {
            throw new DomainException(ProformaErrors.Required, "A delivery with a proforma is invoiced from its proforma (E-FIS1b-01-6).");
        }

        // E-FIS1b-01-8: an authorization that cites proformas covers the invoices of those proformas and nothing else.
        if (authorizationId is { } cited && await SalesSql.ScalarAsync<bool>(
                context,
                """
                SELECT EXISTS (SELECT 1 FROM tax.fiscal_authorization_proforma x WHERE x.authorization_id = @a)
                   AND EXISTS (SELECT 1 FROM unnest(@ids) AS dl (id)
                               WHERE NOT EXISTS (SELECT 1 FROM sal.proforma_line l JOIN tax.fiscal_authorization_proforma x ON x.proforma_id = l.proforma_id AND x.authorization_id = @a
                                                 WHERE l.delivery_line_id = dl.id))
                """,
                cancellationToken, ("a", cited), ("ids", ids.ToArray())).ConfigureAwait(false))
        {
            throw new DomainException(ProformaErrors.NotCertified, "That authorization cites proformas: it covers the invoice of those proformas only (E-FIS1b-01-8).");
        }

        var lines = billable.OrderBy(b => ids.ToList().IndexOf(b.DeliveryLineId))
            .Select(b => (b, Net: decimal.Round(b.Remaining * b.UnitPrice, 2, MidpointRounding.AwayFromZero), LineId: context.Ids.NewId())).ToList();
        var net = lines.Sum(l => l.Net);
        if (authorizationId is { } authorization)
        {
            // E-FIS1-03-1 (D-05): an exempt invoice is exempt as a whole; a line out of the scope is invoiced apart with ITBIS.
            await AuthorizationUsage.CoverAsync(
                context, authorization, partyId, [.. lines.Select(l => new CoveredLine(l.LineId, l.b.ItemId, l.b.Uom, l.b.Remaining, l.Net))], cancellationToken).ConfigureAwait(false);
        }

        var creator = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "invoice-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(invoice_no from 4)::int) FROM sal.invoice WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var invoiceNo = "FA-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var ecfType = authorizationId is null ? Invoicing.DefaultEcfType(rnc) : Invoicing.ExemptEcfType;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "InvoiceCreated",
                1,
                Invoicing.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { invoiceId = context.ResultRef, invoiceNo, partyId = partyId, ecfType, fiscalAuthorizationId = authorizationId, netTotal = Invoicing.M(net), lines = lines.Select(l => new { deliveryLineId = l.b.DeliveryLineId, quantity = Invoicing.M(l.b.Remaining), net = Invoicing.M(l.Net) }) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.invoice (invoice_id, company_id, invoice_no, party_id, ecf_type, commercial_status, accounting_status, fiscal_status, net_total, created_by, version,
                                     fiscal_authorization_id, buyer_name, buyer_id_kind, buyer_id)
            VALUES (@id, @c, @no, @p, @ecf, 'DRAFT', 'NOT_POSTED', 'PENDING', @net, @by, 1, @auth, @bname, @bkind, @bid)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", invoiceNo),
            ("p", partyId),
            ("ecf", ecfType),
            ("net", net),
            ("by", creator),
            ("auth", authorizationId),
            ("bname", buyer?.Name),
            ("bkind", buyer?.IdKind),
            ("bid", buyer?.Id)).ConfigureAwait(false);
        var no = 0;
        foreach (var (b, lineNet, lineId) in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO sal.invoice_line (invoice_line_id, company_id, invoice_id, line_no, delivery_line_id, item_id, uom, quantity, unit_price, net_amount)
                VALUES (@id, @c, @i, @no, @dl, @item, @u, @q, @p, @n)
                """,
                cancellationToken,
                ("id", lineId),
                ("c", context.CompanyId),
                ("i", context.ResultRef),
                ("no", ++no),
                ("dl", b.DeliveryLineId),
                ("item", b.ItemId),
                ("u", b.Uom),
                ("q", b.Remaining),
                ("p", b.UnitPrice),
                ("n", lineNet)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Invoicing.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invoiceId = context.ResultRef, invoiceNo, commercialStatus = "DRAFT", netTotal = Invoicing.M(net), ecfType, version = 1 });
    }
}

[RequiresPermission("invoice:create")]
public sealed class CreateInvoiceFromDeliveriesHandler : ICommandHandler<CreateInvoiceFromDeliveries>
{
    public string CommandType => "Sales.CreateInvoiceFromDeliveries";

    public Task<string> HandleAsync(CreateInvoiceFromDeliveries command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return InvoiceDrafts.CreateAsync(context, CommandType, command.PartyId, command.DeliveryLineIds, command.FiscalAuthorizationId, fromProformas: false, cancellationToken);
    }
}

[RequiresPermission("invoice:create")]
public sealed class CreateInvoiceFromProformasHandler : ICommandHandler<CreateInvoiceFromProformas>
{
    public string CommandType => "Sales.CreateInvoiceFromProformas";

    public async Task<string> HandleAsync(CreateInvoiceFromProformas command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = command.ProformaIds ?? [];
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new DomainException(SalesErrors.LinesRequired, "An invoice from proformas names one or more proformas, each once.");
        }

        // E-FIS1b-01-6: whole OPEN proformas of the customer.
        var proformas = await Allocations.LockProformasAsync(context, ids, cancellationToken).ConfigureAwait(false);
        if (proformas.Any(f => f.PartyId != command.PartyId || f.Status != "OPEN"))
        {
            throw new DomainException(AllocationErrors.ProformaNotOpen, "Each proforma must be an OPEN proforma of this customer.");
        }

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT l.delivery_line_id FROM sal.proforma_line l JOIN sal.proforma p ON p.proforma_id = l.proforma_id WHERE l.proforma_id = ANY (@ids) ORDER BY p.proforma_no, l.line_no",
            r => r.GetGuid(0),
            cancellationToken,
            ("ids", ids.ToArray())).ConfigureAwait(false);
        return await InvoiceDrafts.CreateAsync(context, CommandType, command.PartyId, lines, command.FiscalAuthorizationId, fromProformas: true, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("invoice:issue", StepUp = true)]
public sealed class IssueInvoiceHandler : ICommandHandler<IssueInvoice>
{
    private readonly PostingEngine _engine = new();
    private readonly TaxEngine _tax = new();

    public string CommandType => "Sales.IssueInvoice";

    public async Task<string> HandleAsync(IssueInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        // Lock order: sales order (a cash sale's) → proformas → invoice → AR document → receipts.
        var cashOrder = await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT DISTINCT o.sales_order_id FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
            JOIN log.delivery d ON d.delivery_id = dl.delivery_id JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            WHERE il.company_id = @c AND il.invoice_id = @i AND o.cash_sale
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("i", command.InvoiceId)).ConfigureAwait(false);
        if (cashOrder is { } locked)
        {
            await Orders.Orders.LockCurrentAsync(context, locked, cancellationToken).ConfigureAwait(false);
        }

        // The proformas of the invoice's delivery lines, if any.
        var proformas = await Allocations.LockProformasAsync(
            context,
            await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT DISTINCT l.proforma_id FROM sal.invoice_line il
                JOIN sal.proforma_line l ON l.delivery_line_id = il.delivery_line_id JOIN sal.proforma p ON p.proforma_id = l.proforma_id
                WHERE il.company_id = @c AND il.invoice_id = @i AND p.status <> 'VOIDED'
                """,
                r => r.GetGuid(0),
                cancellationToken,
                ("c", context.CompanyId),
                ("i", command.InvoiceId)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var row = await Invoicing.LockAsync(context, command.InvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Commercial != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The invoice is {row.Commercial}.");
        }

        if (proformas.FirstOrDefault(f => f.Status != "OPEN") is { } taken)
        {
            throw new DomainException(AllocationErrors.ProformaNotOpen, $"{taken.ProformaNo} is {taken.Status}: it was invoiced already.");
        }

        var ecfType = (command.EcfType ?? row.EcfType).Trim();
        var authorization = await Invoicing.AuthorizationAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (authorization is null ? ecfType is not ("31" or "32") : ecfType != Invoicing.ExemptEcfType)
        {
            throw new DomainException(InvoiceErrors.EcfTypeInvalid, authorization is null
                ? "The e-CF type is 31 (crédito fiscal) or 32 (consumo) (E-VS3-05-8); 44 needs a fiscal authorization."
                : "An invoice under a fiscal authorization is an e-CF 44; invoice it anew without the authorization to charge ITBIS (E-FIS1-03-5).");
        }

        // SAL-09: the delivery lines in id order; the invoiced quantity never passes the delivered one. E-FIS1-03-2: the authorization first.
        var lines = await Invoicing.LinesAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false);
        var exemption = authorization is { } auth
            ? await AuthorizationUsage.CoverAsync(context, auth, row.PartyId, Invoicing.Covered(lines), cancellationToken).ConfigureAwait(false)
            : null;
        foreach (var line in lines)
        {
            var remaining = await SalesSql.ScalarAsync<decimal?>(
                context, "SELECT qty_delivered - qty_invoiced FROM log.delivery_line WHERE delivery_line_id = @l FOR UPDATE", cancellationToken, ("l", line.DeliveryLineId)).ConfigureAwait(false);
            if (remaining is null || line.Quantity > remaining)
            {
                throw new DomainException(InvoiceErrors.QuantityExceedsDelivered, $"Delivery {line.DeliveryNo}: {line.Quantity} exceeds what is delivered and not yet invoiced ({remaining}).");
            }
        }

        var today = SalesSql.Today(context);
        // E-CF1-4: a cash sale is due the day it is invoiced; the final consumer has no terms.
        var days = cashOrder is not null
            ? 0
            : await SalesSql.ScalarAsync<int?>(
                context, "SELECT payment_terms_days FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p AND status = 'ACTIVE'", cancellationToken,
                ("c", context.CompanyId), ("p", row.PartyId)).ConfigureAwait(false)
              ?? throw new DomainException(InvoiceErrors.TermsMissing, "The customer has no approved payment terms.");
        var determination = await _tax.DetermineAsync(
            context,
            new TaxRequest("SalesInvoice", command.InvoiceId, today, row.PartyId, lines.Select(l => new TaxLineInput(l.InvoiceLineId, l.ItemId, l.Net)).ToList(), TaxDirections.Sale, exemption),
            cancellationToken).ConfigureAwait(false);
        var itbis = determination.Taxes.Where(t => t.Effect == TaxEffects.Output).Sum(t => t.Amount) + SalesSql.Zero; // "0.00" for an exempt invoice
        var total = row.Net + itbis;
        var arDocId = context.Ids.NewId();

        var inputs = new Dictionary<string, string> { ["invoice_no"] = row.InvoiceNo, ["tax_determination_id"] = determination.DeterminationId.ToString() };
        if (exemption is not null)
        {
            inputs["fiscal_authorization"] = $"{exemption.Regime} {exemption.CertificateNo}";
        }

        var postingLines = new List<PostingLineInput> { new("P18-DR-AR", "invoice_total", total, PartyId: row.PartyId, SubledgerRef: arDocId, Inputs: inputs) };
        foreach (var line in lines)
        {
            // E-VS3-05-5: the role P-16 used for this delivery line, emptied by its full invoicing.
            var p16 = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT account_role, coalesce(sum(debit - credit), 0)::numeric(19,2) FROM fin.gl_entry
                WHERE company_id = @c AND subledger_type = 'AR' AND subledger_ref = @dl AND account_role IN ('CONTRACT_ASSET', 'UNBILLED_RECEIVABLE')
                GROUP BY account_role
                """,
                r => (Role: r.GetString(0), Balance: r.GetDecimal(1)),
                cancellationToken,
                ("c", context.CompanyId),
                ("dl", line.DeliveryLineId)).ConfigureAwait(false);
            if (p16.Count != 1 || p16[0].Balance != line.Net)
            {
                throw new InvalidOperationException($"Delivery line {line.DeliveryLineId}: its unbilled balance does not match the invoiced amount {line.Net}.");
            }

            postingLines.Add(new PostingLineInput(p16[0].Role == "CONTRACT_ASSET" ? "P18-CR-CA" : "P18-CR-UR", "line_net", line.Net, PartyId: row.PartyId, SubledgerRef: line.DeliveryLineId,
                Inputs: new Dictionary<string, string>(inputs) { ["delivery_no"] = line.DeliveryNo }));
        }

        postingLines.Add(new PostingLineInput("P18-CR-ITBIS", "itbis", itbis, Inputs: inputs));
        var plan = await _engine.PrepareAsync(context, new PostingRequest(Invoicing.RuleCode, today, context.Clock.UtcNow, postingLines), cancellationToken).ConfigureAwait(false);

        var issuer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "InvoiceIssued",
                1,
                Invoicing.Aggregate,
                command.InvoiceId,
                version,
                JsonSerializer.Serialize(new { invoiceId = command.InvoiceId, invoiceNo = row.InvoiceNo, ecfType, invoiceDate = today, netTotal = Invoicing.M(row.Net), taxTotal = Invoicing.M(itbis), total = Invoicing.M(total), arDocId }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.ar_document (ar_doc_id, company_id, party_id, doc_type, source_doc_id, doc_no, doc_date, due_date, original_amount, open_amount, version)
            VALUES (@id, @c, @p, 'INVOICE', @src, @no, @date, @due, @amount, @amount, 1)
            """,
            cancellationToken,
            ("id", arDocId),
            ("c", context.CompanyId),
            ("p", row.PartyId),
            ("src", command.InvoiceId),
            ("no", row.InvoiceNo),
            ("date", today),
            ("due", today.AddDays(days)),
            ("amount", total)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE sal.invoice SET commercial_status = 'CONFIRMED', accounting_status = 'POSTED', fiscal_status = 'PENDING_EXTERNAL', invoice_date = @date, due_date = @due,
              ecf_type = @ecf, tax_determination_id = @det, tax_total = @tax, total = @total, ar_doc_id = @ar, posting_event_id = @e, issued_by = @by, version = @v
            WHERE invoice_id = @i
            """,
            cancellationToken,
            ("date", today),
            ("due", today.AddDays(days)),
            ("ecf", ecfType),
            ("det", determination.DeterminationId),
            ("tax", itbis),
            ("total", total),
            ("ar", arDocId),
            ("e", eventId),
            ("by", issuer),
            ("v", version),
            ("i", command.InvoiceId)).ConfigureAwait(false);
        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE log.delivery_line SET qty_invoiced = qty_invoiced + @q WHERE delivery_line_id = @l", cancellationToken, ("q", line.Quantity), ("l", line.DeliveryLineId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE sal.sales_order_line SET qty_invoiced = qty_invoiced + @q WHERE line_id = @l", cancellationToken, ("q", line.Quantity), ("l", line.OrderLineId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO core.document_link (link_id, company_id, from_type, from_id, from_line_id, to_type, to_id, to_line_id, link_type, qty, amount, event_id)
                VALUES (@id, @c, 'Invoice', @i, @il, 'Delivery', (SELECT delivery_id FROM log.delivery_line WHERE delivery_line_id = @dl), @dl, 'INVOICES', @q, @n, @e)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("i", command.InvoiceId),
                ("il", line.InvoiceLineId),
                ("dl", line.DeliveryLineId),
                ("q", line.Quantity),
                ("n", line.Net),
                ("e", eventId)).ConfigureAwait(false);
        }

        if (exemption is not null)
        {
            // E-FIS1-03-4: the consumption of the authorization, in the issue's transaction.
            await AuthorizationUsage.ConsumeAsync(context, exemption.AuthorizationId, Invoicing.Covered(lines), eventId, CommandType, cancellationToken).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Invoicing.Aggregate, command.InvoiceId, "DOCUMENT", "DRAFT", "CONFIRMED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Invoicing.Aggregate, command.InvoiceId, "ACCOUNTING", "NOT_POSTED", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);

        // E-FIS1b-7, E-FIS1b-01-7: the invoice of proformas inherits what was collected on them.
        var commercial = "CONFIRMED";
        var inherited = SalesSql.Zero;
        if (proformas.Count > 0)
        {
            inherited += await ProformaInvoicing.InheritAsync(
                context, _engine, command.InvoiceId, row.InvoiceNo, arDocId, total, proformas, eventId, CommandType, cancellationToken).ConfigureAwait(false);
            if (inherited > 0m)
            {
                commercial = await InvoiceStanding.RefreshAsync(context, command.InvoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
                version++;
            }
        }

        // E-CF1-5, E-CF1-13: the invoice of a cash order takes what it needs of the receipts assigned to the order.
        var fromOrder = SalesSql.Zero;
        if (cashOrder is { } paidOrder)
        {
            fromOrder += await CashSales.CashSaleStore.InheritAsync(context, _engine, paidOrder, command.InvoiceId, row.InvoiceNo, arDocId, total, eventId, CommandType, cancellationToken)
                .ConfigureAwait(false);
            if (fromOrder > 0m)
            {
                commercial = await InvoiceStanding.RefreshAsync(context, command.InvoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
                version++;
            }
        }

        return JsonSerializer.Serialize(new
        {
            invoiceId = command.InvoiceId,
            commercialStatus = commercial,
            accountingStatus = "POSTED",
            fiscalStatus = "PENDING_EXTERNAL",
            netTotal = Invoicing.M(row.Net),
            taxTotal = Invoicing.M(itbis),
            total = Invoicing.M(total),
            collectedOnProformas = Invoicing.M(inherited),
            collectedOnOrder = Invoicing.M(fromOrder),
            journalId = journal.JournalId,
            version,
        });
    }
}

[RequiresPermission("fiscal_document:record")]
public sealed class RecordExternalFiscalDocumentHandler : ICommandHandler<RecordExternalFiscalDocument>
{
    public string CommandType => "Sales.RecordExternalFiscalDocument";

    public async Task<string> HandleAsync(RecordExternalFiscalDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Invoicing.LockAsync(context, command.InvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Commercial is not ("CONFIRMED" or "PARTIALLY_PAID" or "PAID") || row.Fiscal != "PENDING_EXTERNAL")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The invoice is {row.Commercial} / {row.Fiscal}; an external e-CF is recorded for an issued invoice pending it.");
        }

        var encf = (command.Encf ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(encf, "^E" + row.EcfType + "[0-9]{10}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            throw new DomainException(InvoiceErrors.EncfInvalid, $"The e-NCF is E{row.EcfType} followed by 10 digits (E-VS3-05-8).");
        }

        var security = SalesSql.Optional(command.SecurityCode, 60, "The security code") ?? throw new DomainException(InvoiceErrors.FiscalDocumentMismatch, "The security code is required.");
        var evidence = SalesSql.Optional(command.EvidenceRef, 200, "The evidence reference") ?? throw new DomainException(DeliveryErrors.EvidenceInvalid, "The XML or PDF reference is required.");
        var hash = Deliveries.Deliveries.Sha256(command.EvidenceSha256);
        if (command.IssuedAt > context.Clock.UtcNow)
        {
            throw new DomainException(InvoiceErrors.FiscalDocumentMismatch, "The e-CF cannot be issued in the future.");
        }

        var expected = await FiscalReceiver.ExpectedAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false);
        var (receiver, passport) = FiscalReceiver.Given(command.ReceiverRnc, command.ReceiverPassport);
        var differences = new List<string>();
        differences.AddRange(FiscalReceiver.Differences(expected, receiver, passport));

        if (command.NetTotal != row.Net)
        {
            differences.Add($"net {Invoicing.M(command.NetTotal)} ≠ {Invoicing.M(row.Net)}");
        }

        if (command.TaxTotal != row.Tax)
        {
            differences.Add($"ITBIS {Invoicing.M(command.TaxTotal)} ≠ {Invoicing.M(row.Tax!.Value)}");
        }

        if (command.Total != row.Total)
        {
            differences.Add($"total {Invoicing.M(command.Total)} ≠ {Invoicing.M(row.Total!.Value)}");
        }

        if (differences.Count > 0)
        {
            throw new DomainException(InvoiceErrors.FiscalDocumentMismatch, $"The e-CF does not match the invoice ({string.Join("; ", differences)}); the invoice stays PENDING_EXTERNAL (SAL-07).");
        }

        if (await SalesSql.ScalarAsync<Guid?>(context, "SELECT invoice_id FROM sal.invoice WHERE company_id = @c AND encf = @e", cancellationToken, ("c", context.CompanyId), ("e", encf)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(InvoiceErrors.EncfDuplicate, $"e-NCF {encf} is already recorded (E-VS3-9).");
        }

        var recorder = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ExternalFiscalDocumentIssued",
                1,
                Invoicing.Aggregate,
                command.InvoiceId,
                version,
                JsonSerializer.Serialize(new { invoiceId = command.InvoiceId, invoiceNo = row.InvoiceNo, encf, issuedAt = command.IssuedAt, evidenceRef = evidence, total = Invoicing.M(row.Total!.Value) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        var recordId = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.external_fiscal_record (record_id, company_id, invoice_id, encf, issued_at, security_code, evidence_ref, evidence_sha256, receiver_rnc, net_total, tax_total, total, recorded_by, event_id,
                                                    receiver_passport)
            VALUES (@id, @c, @i, @e, @at, @sec, @ref, @hash, @rnc, @net, @tax, @total, @by, @ev, @passport)
            """,
            cancellationToken,
            ("id", recordId),
            ("c", context.CompanyId),
            ("i", command.InvoiceId),
            ("e", encf),
            ("at", command.IssuedAt),
            ("sec", security),
            ("ref", evidence),
            ("hash", hash),
            ("rnc", receiver.Length == 0 ? null : receiver),
            ("passport", passport),
            ("net", command.NetTotal),
            ("tax", command.TaxTotal),
            ("total", command.Total),
            ("by", recorder),
            ("ev", eventId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.invoice SET fiscal_status = 'ACCEPTED_EXTERNAL', encf = @e, version = @v WHERE invoice_id = @i", cancellationToken,
            ("e", encf), ("v", version), ("i", command.InvoiceId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, link_type, event_id)
            VALUES (@id, @c, 'ExternalFiscalRecord', @r, 'Invoice', @i, 'FISCALIZES', @e)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("r", recordId),
            ("i", command.InvoiceId),
            ("e", eventId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invoiceId = command.InvoiceId, fiscalStatus = "ACCEPTED_EXTERNAL", encf, version });
    }
}

[RequiresPermission("invoice:void", StepUp = true)]
public sealed class VoidUnfiscalizedInvoiceHandler : ICommandHandler<VoidUnfiscalizedInvoice>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.VoidUnfiscalizedInvoice";

    public async Task<string> HandleAsync(VoidUnfiscalizedInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = SalesSql.Optional(command.Reason, 500, "The reason") ?? throw new DomainException(Orders.OrderErrors.ReasonRequired, "Voiding an invoice needs a reason.");
        var proformas = await Allocations.LockProformasAsync(
            context,
            await Reading.ListAsync(
                context.Connection, context.Transaction, "SELECT proforma_id FROM sal.proforma WHERE company_id = @c AND invoice_id = @i", r => r.GetGuid(0), cancellationToken,
                ("c", context.CompanyId), ("i", command.InvoiceId)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var row = await Invoicing.LockAsync(context, command.InvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Commercial != "CONFIRMED" || row.Fiscal != "PENDING_EXTERNAL")
        {
            throw new DomainException(InvoiceErrors.NotVoidable, $"Only an issued, never fiscalized invoice is voided (it is {row.Commercial} / {row.Fiscal}; a fiscalized one needs a credit note).");
        }

        var ar = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT original_amount, open_amount, version FROM fin.ar_document WHERE ar_doc_id = @a FOR UPDATE",
            r => new[] { r.GetDecimal(0), r.GetDecimal(1), r.GetInt64(2) },
            cancellationToken,
            ("a", row.ArDocId!.Value)).ConfigureAwait(false)!;
        if (ar![1] != ar[0])
        {
            throw new DomainException(InvoiceErrors.NotVoidable, "The invoice has receipts applied; it cannot be voided.");
        }

        var journal = await SalesSql.ScalarAsync<Guid?>(
            context,
            "SELECT journal_id FROM fin.gl_journal j WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO' AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)",
            cancellationToken,
            ("c", context.CompanyId),
            ("e", row.PostingEventId!.Value)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.InvalidState, "The invoice has no live journal.");
        var occurredAt = context.Clock.UtcNow;
        var plan = await _engine.PrepareReversalAsync(context, journal, SalesSql.Today(context), cancellationToken).ConfigureAwait(false);
        var lines = await Invoicing.LinesAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("InvoiceVoided", 1, Invoicing.Aggregate, command.InvoiceId, version, JsonSerializer.Serialize(new { invoiceId = command.InvoiceId, invoiceNo = row.InvoiceNo, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.ar_document SET open_amount = 0, version = version + 1 WHERE ar_doc_id = @a", cancellationToken, ("a", row.ArDocId.Value)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE sal.invoice SET commercial_status = 'VOIDED', accounting_status = 'REVERSED', void_event_id = @e, void_reason = @r, version = @v WHERE invoice_id = @i",
            cancellationToken,
            ("e", eventId),
            ("r", reason),
            ("v", version),
            ("i", command.InvoiceId)).ConfigureAwait(false);
        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE log.delivery_line SET qty_invoiced = qty_invoiced - @q WHERE delivery_line_id = @l", cancellationToken, ("q", line.Quantity), ("l", line.DeliveryLineId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE sal.sales_order_line SET qty_invoiced = qty_invoiced - @q WHERE line_id = @l", cancellationToken, ("q", line.Quantity), ("l", line.OrderLineId)).ConfigureAwait(false);
        }

        if (await Invoicing.AuthorizationAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false) is { } authorization)
        {
            // E-FIS1-03-7: the void returns everything the invoice still consumes.
            await AuthorizationUsage.ReleaseAsync(
                context, authorization, [.. lines.Select(l => new ReleasedLine(l.InvoiceLineId, l.Quantity, l.Net))], eventId, CommandType, cancellationToken).ConfigureAwait(false);
        }

        // E-FIS1b-1: the proformas of a voided invoice are open again, to be invoiced anew.
        await ProformaInvoicing.SetStatusAsync(context, proformas.Where(f => f.Status == "INVOICED").ToList(), "OPEN", null, eventId, CommandType, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Invoicing.Aggregate, command.InvoiceId, "DOCUMENT", "CONFIRMED", "VOIDED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await context.AppendStateAsync(Invoicing.Aggregate, command.InvoiceId, "ACCOUNTING", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, occurredAt, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invoiceId = command.InvoiceId, commercialStatus = "VOIDED", accountingStatus = "REVERSED", reversalJournalId = reversal.JournalId, version });
    }
}
