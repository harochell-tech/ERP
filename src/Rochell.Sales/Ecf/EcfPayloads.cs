using System.Globalization;
using System.Text.Json.Nodes;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Tax.Ecf;

namespace Rochell.Sales.Ecf;

// VS4-03 (E-VS4-01-10, E-VS4-03-2…11): the e-CF 31 / 32 / 34 / 44 Alanube receives, built from the issued invoice or credit note inside
// the issuing command. Amounts without ITBIS (IndicadorMontoGravado 0), each line with its billing indicator (1 the sales ITBIS rate,
// 4 exempt), 2 decimals; the totals are checked against the document to the cent before anything is queued.

internal static class EcfPayloads
{
    private const int NameMax = 150;
    private const int ItemNameMax = 80;
    private const int InternalNumberMax = 20;
    private const int BuyerInfoMax = 150;
    private const int PdfNoteMax = 250;
    private const int ReasonMax = 90;
    private const int CreditNoteDays = 30;
    private const int TaxedIndicator = 1;
    private const int ExemptIndicator = 4;
    private const int Good = 1;
    private const int Service = 2;
    private const int OperatingIncome = 1;
    private const int Cash = 1;
    private const int Credit = 2;
    private const int FormCash = 1;
    private const int FormBank = 2;
    private const int FormCredit = 4;
    private const int FullCancellation = 1;
    private const int AmountCorrection = 3;

    public sealed record Issuer(string Rnc, string Name, string Address, string? TradeName, string? Phone, string? Email);

    private sealed record Head(
        string No, string Type, DateOnly Date, DateOnly Due, Guid PartyId, string PartyKind, string? Rnc, string LegalName, string? BuyerName, string? BuyerIdKind, string? BuyerId,
        decimal Net, decimal Tax, decimal Total, string? Certificate);

    private sealed record ItemLine(int No, string Code, string Name, bool Service, int? Unit, decimal Quantity, decimal UnitPrice, decimal Net, decimal Rate, decimal Itbis);

    /// <summary>The e-CF of an issued invoice, as a function of its e-NCF and its range's due date (EcfQueue numbers it).</summary>
    public static async Task<Func<string, DateOnly, JsonObject>> InvoiceAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
    {
        var issuer = await IssuerAsync(context, cancellationToken).ConfigureAwait(false);
        var h = await HeadAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT il.line_no, i.code, i.description, il.line_kind = 'FREIGHT' OR i.item_type = 'SERVICE', u.dgii_code, il.quantity, il.unit_price, il.net_amount::numeric(19,2),
                   coalesce(t.rate, 0), coalesce(t.amount, 0)::numeric(19,2)
            FROM sal.invoice_line il JOIN md.item i ON i.item_id = il.item_id LEFT JOIN md.uom u ON u.uom_code = il.uom
            LEFT JOIN sal.invoice inv ON inv.invoice_id = il.invoice_id
            LEFT JOIN tax.tax_determination_line t ON t.determination_id = inv.tax_determination_id AND t.subject_line_id = il.invoice_line_id AND t.effect = 'OUTPUT'
            WHERE il.invoice_id = @i ORDER BY il.line_no
            """,
            r => new ItemLine(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                r.GetDecimal(8), r.GetDecimal(9)),
            cancellationToken,
            ("i", invoiceId)).ConfigureAwait(false);
        var forms = h.Type == "34" ? [] : await PaymentFormsAsync(context, invoiceId, h, cancellationToken).ConfigureAwait(false);
        var body = Body(issuer, h, lines, h.Date, h.No);
        Check(h, lines);
        return (encf, due) =>
        {
            var payload = (JsonObject)body.DeepClone();
            payload["idDoc"] = InvoiceIdDoc(h, encf, due, forms);
            return payload;
        };
    }

    /// <summary>The e-CF 34 of an issued credit note: the original e-CF it corrects, code 1 or 3, the reason (E-VS4-03-8).</summary>
    public static async Task<Func<string, DateOnly, JsonObject>> CreditNoteAsync(CommandContext context, Guid creditNoteId, CancellationToken cancellationToken)
    {
        var issuer = await IssuerAsync(context, cancellationToken).ConfigureAwait(false);
        var note = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT n.credit_note_no, n.credit_date, n.invoice_id, n.reason, n.net_total::numeric(19,2), n.tax_total::numeric(19,2), n.total::numeric(19,2), i.encf
            FROM sal.credit_note n JOIN sal.invoice i ON i.invoice_id = n.invoice_id WHERE n.company_id = @c AND n.credit_note_id = @n
            """,
            r => (No: r.GetString(0), Date: r.Date(1), InvoiceId: r.GetGuid(2), Reason: r.GetString(3), Net: r.GetDecimal(4), Tax: r.GetDecimal(5), Total: r.GetDecimal(6),
                InvoiceEncf: r.NullableString(7)),
            cancellationToken,
            ("c", context.CompanyId),
            ("n", creditNoteId)).ConfigureAwait(false)).Single();
        var invoice = await HeadAsync(context, note.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (note.InvoiceEncf is null)
        {
            throw new DomainException(EcfErrors.PayloadInvalid, $"Invoice {invoice.No} has no accepted e-CF: a credit note corrects an accepted e-CF (E-VS4-7).");
        }

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT nl.line_no, i.code, i.description, il.line_kind = 'FREIGHT' OR i.item_type = 'SERVICE', u.dgii_code,
                   CASE WHEN nl.net_amount = il.net_amount THEN il.quantity ELSE 1::numeric(18,6) END, CASE WHEN nl.net_amount = il.net_amount THEN il.unit_price ELSE nl.net_amount END,
                   nl.net_amount::numeric(19,2), nl.rate, nl.itbis::numeric(19,2)
            FROM sal.credit_note_line nl JOIN sal.invoice_line il ON il.invoice_line_id = nl.invoice_line_id JOIN md.item i ON i.item_id = il.item_id
            LEFT JOIN md.uom u ON u.uom_code = il.uom
            WHERE nl.credit_note_id = @n ORDER BY nl.line_no
            """,
            r => new ItemLine(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                r.GetDecimal(8), r.GetDecimal(9)),
            cancellationToken,
            ("n", creditNoteId)).ConfigureAwait(false);
        var h = invoice with { No = note.No, Type = "34", Date = note.Date, Net = note.Net, Tax = note.Tax, Total = note.Total, Certificate = null };
        Check(h, lines);
        var body = Body(issuer, h, lines, note.Date, note.No);
        var whole = note.Net == invoice.Net && note.Tax == invoice.Tax;
        var reason = note.Reason.Trim();
        var reference = new JsonObject
        {
            ["ncfModified"] = note.InvoiceEncf,
            ["ncfModifiedDate"] = D(invoice.Date),
            ["modificationCode"] = whole ? FullCancellation : AmountCorrection,
            ["reasonForModification"] = reason.Length > ReasonMax ? reason[..ReasonMax] : reason,
        };
        var late = note.Date > invoice.Date.AddDays(CreditNoteDays);
        return (encf, _) =>
        {
            var payload = (JsonObject)body.DeepClone();
            payload["idDoc"] = new JsonObject
            {
                ["encf"] = encf,
                ["creditNoteIndicator"] = late ? 1 : 0,
                ["taxAmountIndicator"] = 0,
                ["incomeType"] = OperatingIncome,
                ["paymentType"] = invoice.Due > invoice.Date ? Credit : Cash,
            };
            payload["informationReference"] = reference.DeepClone();
            return payload;
        };
    }

    private static async Task<Issuer> IssuerAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var issuer = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT rnc, legal_name, address, trade_name, phone, email FROM md.company WHERE company_id = @c",
            r => new Issuer(r.GetString(0), r.GetString(1), r.NullableString(2) ?? string.Empty, r.NullableString(3), r.NullableString(4), r.NullableString(5)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).Single();
        return issuer.Address.Length > 0
            ? issuer
            : throw new DomainException(EcfErrors.IssuerIncomplete, "The company has no address: set it in Configuración › Empresa before issuing e-CF (E-VS4-03-2).");
    }

    private static async Task<Head> HeadAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.invoice_no, i.ecf_type, i.invoice_date, i.due_date, i.party_id, p.party_kind, p.rnc, p.legal_name, i.buyer_name, i.buyer_id_kind, i.buyer_id,
                   i.net_total::numeric(19,2), i.tax_total::numeric(19,2), i.total::numeric(19,2), a.certificate_no
            FROM sal.invoice i JOIN md.party p ON p.party_id = i.party_id LEFT JOIN tax.fiscal_authorization a ON a.authorization_id = i.fiscal_authorization_id
            WHERE i.company_id = @c AND i.invoice_id = @i
            """,
            r => new Head(r.GetString(0), r.GetString(1), r.Date(2), r.Date(3), r.GetGuid(4), r.GetString(5), r.NullableString(6), r.GetString(7), r.NullableString(8),
                r.NullableString(9), r.NullableString(10), r.GetDecimal(11), r.GetDecimal(12), r.GetDecimal(13), r.NullableString(14)),
            cancellationToken,
            ("c", context.CompanyId),
            ("i", invoiceId)).ConfigureAwait(false)).Single();

    /// <summary>
    /// E-VS4-03-5: a cash sale's forms are the receipts its order's assignments applied to the invoice at issue (cash 1, cheque or
    /// transfer 2), up to the invoice total; a credit sale is form 4 for the whole total.
    /// </summary>
    private static async Task<List<(int Form, decimal Amount)>> PaymentFormsAsync(CommandContext context, Guid invoiceId, Head h, CancellationToken cancellationToken)
    {
        if (h.Due > h.Date)
        {
            return [(FormCredit, h.Total)];
        }

        var assigned = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.method, a.amount::numeric(19,2) FROM fin.ar_application a JOIN fin.receipt r ON r.receipt_id = a.receipt_id
            JOIN sal.invoice i ON i.ar_doc_id = a.ar_doc_id
            WHERE i.invoice_id = @i AND a.reverses_application_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.ar_application x WHERE x.reverses_application_id = a.application_id)
            ORDER BY a.application_id
            """,
            r => (Method: r.GetString(0), Amount: r.GetDecimal(1)),
            cancellationToken,
            ("i", invoiceId)).ConfigureAwait(false);
        var forms = new List<(int Form, decimal Amount)>();
        var left = h.Total;
        foreach (var (method, amount) in assigned)
        {
            if (left <= 0m)
            {
                break;
            }

            var take = Math.Min(left, amount);
            var form = method == "CASH" ? FormCash : FormBank;
            var at = forms.FindIndex(f => f.Form == form);
            if (at >= 0)
            {
                forms[at] = (form, forms[at].Amount + take);
            }
            else
            {
                forms.Add((form, take));
            }

            left -= take;
        }

        if (left > 0m)
        {
            // Paid on the day without assigned receipts (a counter sale with terms of 0 days): cash for what is left.
            forms.Add((FormCash, left));
        }

        return forms;
    }

    private static JsonObject InvoiceIdDoc(Head h, string encf, DateOnly due, List<(int Form, decimal Amount)> forms)
    {
        var idDoc = new JsonObject { ["encf"] = encf };
        if (h.Type != "32")
        {
            idDoc["sequenceDueDate"] = D(due);
        }

        if (h.Type != "44" && h.Tax > 0m)
        {
            idDoc["taxAmountIndicator"] = 0;
        }

        idDoc["incomeType"] = OperatingIncome;
        var credit = h.Due > h.Date;
        idDoc["paymentType"] = credit ? Credit : Cash;
        if (credit)
        {
            idDoc["paymentDeadline"] = D(h.Due);
            idDoc["paymentTerm"] = $"{h.Due.DayNumber - h.Date.DayNumber} días";
        }

        idDoc["paymentFormsTable"] = new JsonArray([.. forms.Select(f => (JsonNode)new JsonObject { ["paymentMethod"] = f.Form, ["paymentAmount"] = f.Amount })]);
        return idDoc;
    }

    private static JsonObject Body(Issuer issuer, Head h, List<ItemLine> lines, DateOnly date, string internalNo)
    {
        var sender = new JsonObject
        {
            ["rnc"] = issuer.Rnc,
            ["companyName"] = Cut(issuer.Name, NameMax),
        };
        if (issuer.TradeName is not null)
        {
            sender["tradename"] = issuer.TradeName;
        }

        sender["address"] = issuer.Address;
        if (issuer.Phone is not null)
        {
            sender["phoneNumber"] = new JsonArray(issuer.Phone);
        }

        if (issuer.Email is not null)
        {
            sender["mail"] = issuer.Email;
        }

        sender["internalInvoiceNumber"] = Cut(internalNo, InternalNumberMax);
        sender["stampDate"] = D(date);

        var payload = new JsonObject { ["sender"] = sender };
        if (Buyer(h) is { } buyer)
        {
            payload["buyer"] = buyer;
        }

        var exempt = h.Type == "44";
        var taxed = lines.Where(l => l.Rate > 0m).ToList();
        var totals = new JsonObject();
        if (taxed.Count > 0)
        {
            totals["totalTaxedAmount"] = taxed.Sum(l => l.Net);
            totals["i1AmountTaxed"] = taxed.Sum(l => l.Net);
        }

        var exemptLines = lines.Where(l => l.Rate == 0m).ToList();
        if (exemptLines.Count > 0)
        {
            totals["exemptAmount"] = exemptLines.Sum(l => l.Net);
        }

        if (taxed.Count > 0)
        {
            totals["itbisS1"] = (int)(taxed[0].Rate * 100m);
            totals["itbisTotal"] = taxed.Sum(l => l.Itbis);
            totals["itbis1Total"] = taxed.Sum(l => l.Itbis);
        }

        totals["totalAmount"] = h.Total;
        payload["totals"] = totals;
        payload["itemDetails"] = new JsonArray([.. lines.Select((l, i) => (JsonNode)Item(l, i + 1, exempt))]);
        if (exempt && h.Certificate is not null)
        {
            payload["config"] = new JsonObject { ["pdf"] = new JsonObject { ["note"] = Cut($"Exento de ITBIS por CONFOTUR, certificación {h.Certificate}.", PdfNoteMax) } };
        }

        return payload;
    }

    private static JsonObject Item(ItemLine l, int number, bool exempt)
    {
        var item = new JsonObject
        {
            ["lineNumber"] = number,
            ["itemCodeTable"] = new JsonArray(new JsonObject { ["codeType"] = "Interna", ["itemCode"] = Cut(l.Code, InternalNumberMax) }),
            ["billingIndicator"] = exempt || l.Rate == 0m ? ExemptIndicator : TaxedIndicator,
            ["itemName"] = Cut(l.Name, ItemNameMax),
            ["goodServiceIndicator"] = l.Service ? Service : Good,
            ["quantityItem"] = Math.Round(l.Quantity, 2),
        };
        if (l.Unit is { } unit)
        {
            item["unitMeasure"] = unit;
        }

        item["unitPriceItem"] = Math.Round(l.UnitPrice, 4);
        item["itemAmount"] = l.Net;
        return item;
    }

    /// <summary>E-VS4-03-6: on 31 / 44 the customer (RNC); on 32 only an identified buyer (cédula or RNC; a passport as foreign identifier). Never the e-mail.</summary>
    private static JsonObject? Buyer(Head h)
    {
        var consumer = h.PartyKind == "CONSUMER";
        var name = Cut(consumer ? h.BuyerName ?? h.LegalName : h.LegalName, NameMax);
        JsonObject? buyer = null;
        if (consumer && h.BuyerIdKind == "PASAPORTE" && h.BuyerId is not null)
        {
            buyer = new JsonObject { ["foreignIdentifier"] = h.BuyerId, ["companyName"] = name };
        }
        else if (consumer ? h.BuyerIdKind is "CEDULA" or "RNC" && h.BuyerId is not null : h.Rnc is not null)
        {
            buyer = new JsonObject { ["rnc"] = consumer ? h.BuyerId : h.Rnc, ["companyName"] = name };
        }

        if (buyer is not null && h.Type == "44" && h.Certificate is not null)
        {
            buyer["additionalInformation"] = Cut($"CONFOTUR certificación {h.Certificate}", BuyerInfoMax);
        }

        return buyer;
    }

    /// <summary>E-VS4-03-4/11: quantities of at most 2 decimals, one ITBIS rate, and totals equal to the document's to the cent.</summary>
    private static void Check(Head h, List<ItemLine> lines)
    {
        if (lines.FirstOrDefault(l => l.Quantity != Math.Round(l.Quantity, 2)) is { } fine)
        {
            throw new DomainException(EcfErrors.PayloadInvalid, $"Line {fine.No}: the quantity {M(fine.Quantity)} has more than 2 decimals, which the e-CF does not take (E-VS4-03-4).");
        }

        if (lines.Where(l => l.Rate > 0m).Select(l => l.Rate).Distinct().Count() > 1)
        {
            throw new DomainException(EcfErrors.PayloadInvalid, "The lines carry more than one ITBIS rate; the e-CF of a sale takes one (E-VS4-01-10).");
        }

        if (h.Type == "44" && lines.Any(l => l.Rate > 0m))
        {
            throw new DomainException(EcfErrors.PayloadInvalid, "An e-CF 44 has every line exempt (E-VS4-03-9).");
        }

        var net = lines.Sum(l => l.Net);
        var itbis = lines.Sum(l => l.Itbis);
        if (net != h.Net || itbis != h.Tax || net + itbis != h.Total)
        {
            throw new DomainException(
                EcfErrors.PayloadInvalid,
                $"The e-CF does not match {h.No}: net {M(net)} / {M(h.Net)}, ITBIS {M(itbis)} / {M(h.Tax)}, total {M(net + itbis)} / {M(h.Total)} (E-VS4-03-11).");
        }
    }

    private static string D(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string M(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cut(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
