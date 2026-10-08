using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Ecf;
using Rochell.Sales.Invoices;
using Rochell.Tax.Authorizations;
using Rochell.Tax.Ecf;

namespace Rochell.Sales.CreditNotes;

public static class CreditNoteErrors
{
    public const string InvoiceNotCreditable = "INVOICE_NOT_CREDITABLE";
    public const string ExceedsCreditable = "CREDIT_EXCEEDS_INVOICE_LINE";
    public const string ExceedsOpenReceivable = "CREDIT_EXCEEDS_OPEN_RECEIVABLE";
    public const string ReasonInvalid = "CREDIT_NOTE_REASON_INVALID";
}

internal static class Crediting
{
    public const string Aggregate = "CreditNote";
    public const string RuleCode = "P-22";

    /// <summary>What an invoice line had and what CONFIRMED credit notes already took from it (E-VS3-06-1/2).</summary>
    public sealed record LineState(Guid InvoiceLineId, decimal Net, decimal Itbis, decimal Rate, decimal CreditedNet, decimal CreditedItbis)
    {
        public decimal RemainingNet => Net - CreditedNet;

        public decimal RemainingItbis => Itbis - CreditedItbis;

        /// <summary>The ITBIS of crediting <paramref name="net"/>: the rest when the line is fully credited, else net × rate, never above the rest.</summary>
        public decimal ItbisFor(decimal net)
            => net == RemainingNet ? RemainingItbis : Math.Min(RemainingItbis, decimal.Round(net * Rate, 2, MidpointRounding.AwayFromZero));
    }

    public static Task<List<LineState>> LinesAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT il.invoice_line_id, il.net_amount::numeric(19,2), coalesce(t.amount, 0)::numeric(19,2), coalesce(t.rate, 0),
                   coalesce((SELECT sum(cl.net_amount) FROM sal.credit_note_line cl JOIN sal.credit_note n ON n.credit_note_id = cl.credit_note_id
                             WHERE cl.invoice_line_id = il.invoice_line_id AND n.commercial_status = 'CONFIRMED'), 0)::numeric(19,2),
                   coalesce((SELECT sum(cl.itbis) FROM sal.credit_note_line cl JOIN sal.credit_note n ON n.credit_note_id = cl.credit_note_id
                             WHERE cl.invoice_line_id = il.invoice_line_id AND n.commercial_status = 'CONFIRMED'), 0)::numeric(19,2)
            FROM sal.invoice_line il
            JOIN sal.invoice i ON i.invoice_id = il.invoice_id
            LEFT JOIN tax.tax_determination_line t ON t.determination_id = i.tax_determination_id AND t.subject_line_id = il.invoice_line_id AND t.effect = 'OUTPUT'
            WHERE il.invoice_id = @i
            """,
            r => new LineState(r.GetGuid(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)),
            cancellationToken,
            ("i", invoiceId));

    public sealed record Invoice(Guid PartyId, string InvoiceNo, string Commercial, string Fiscal, string? Encf, decimal Total, Guid? ArDocId, Guid? IssuedBy, long Version);

    public static async Task<Invoice> LockInvoiceAsync(CommandContext context, Guid invoiceId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               """
               SELECT party_id, invoice_no, commercial_status, fiscal_status, encf, coalesce(total, 0)::numeric(19,2), ar_doc_id, issued_by, version
               FROM sal.invoice WHERE company_id = @c AND invoice_id = @i FOR UPDATE
               """,
               r => new Invoice(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.NullableString(4), r.GetDecimal(5), r.NullableGuid(6), r.NullableGuid(7), r.GetInt64(8)),
               cancellationToken,
               ("c", context.CompanyId),
               ("i", invoiceId)).ConfigureAwait(false)
           ?? throw new DomainException(SalesErrors.NotFound, "The invoice does not exist.");

    public static void EnsureCreditable(Invoice invoice)
    {
        if (invoice.Commercial is not ("CONFIRMED" or "PARTIALLY_PAID" or "PAID") || invoice.Fiscal is not ("ACCEPTED_EXTERNAL" or "ECF_ACCEPTED"))
        {
            throw new DomainException(CreditNoteErrors.InvoiceNotCreditable, $"A credit note needs a fiscalized, not credited invoice (it is {invoice.Commercial} / {invoice.Fiscal}, E-VS3-06-1).");
        }
    }

    public static string M(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

[RequiresPermission("credit_note:create")]
public sealed class CreateCreditNoteHandler : ICommandHandler<CreateCreditNote>
{
    public string CommandType => "Sales.CreateCreditNote";

    public async Task<string> HandleAsync(CreateCreditNote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var category = (command.ReasonCategory ?? string.Empty).Trim().ToUpperInvariant();
        var reason = SalesSql.Optional(command.Reason, 500, "The reason");
        if (category is not ("DESCUENTO" or "ERROR_DE_PRECIO" or "OTRO") || reason is null)
        {
            throw new DomainException(CreditNoteErrors.ReasonInvalid, "The reason category is DESCUENTO, ERROR_DE_PRECIO or OTRO, with its text.");
        }

        var input = command.Lines ?? [];
        if (input.Count == 0 || input.Select(l => l.InvoiceLineId).Distinct().Count() != input.Count
            || input.Any(l => l.NetAmount <= 0m || decimal.Round(l.NetAmount, 2) != l.NetAmount))
        {
            throw new DomainException(SalesErrors.LinesRequired, "A credit note has lines, each invoice line once, with a positive net of at most 2 decimals.");
        }

        var invoice = await Crediting.LockInvoiceAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false);
        Crediting.EnsureCreditable(invoice);
        var states = await Crediting.LinesAsync(context, command.InvoiceId, cancellationToken).ConfigureAwait(false);
        var lines = new List<(Crediting.LineState State, decimal Net, decimal Itbis)>();
        foreach (var l in input)
        {
            var state = states.SingleOrDefault(s => s.InvoiceLineId == l.InvoiceLineId)
                ?? throw new DomainException(SalesErrors.NotFound, "A credited line does not belong to the invoice.");
            if (l.NetAmount > state.RemainingNet)
            {
                throw new DomainException(CreditNoteErrors.ExceedsCreditable, $"{Crediting.M(l.NetAmount)} exceeds the {Crediting.M(state.RemainingNet)} the line still has to credit.");
            }

            lines.Add((state, l.NetAmount, state.ItbisFor(l.NetAmount)));
        }

        var (net, tax) = (SalesSql.Zero + lines.Sum(l => l.Net), SalesSql.Zero + lines.Sum(l => l.Itbis));
        var creator = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "credit-note-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(credit_note_no from 4)::int) FROM sal.credit_note WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var number = "NC-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CreditNoteCreated",
                1,
                Crediting.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { creditNoteId = context.ResultRef, creditNoteNo = number, invoiceId = command.InvoiceId, invoiceNo = invoice.InvoiceNo, reasonCategory = category, reason, netTotal = Crediting.M(net), taxTotal = Crediting.M(tax) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.credit_note (credit_note_id, company_id, credit_note_no, invoice_id, party_id, reason_category, reason, commercial_status, accounting_status, fiscal_status,
              net_total, tax_total, total, created_by, version)
            VALUES (@id, @c, @no, @i, @p, @cat, @reason, 'DRAFT', 'NOT_POSTED', 'PENDING', @net, @tax, @total, @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", number),
            ("i", command.InvoiceId),
            ("p", invoice.PartyId),
            ("cat", category),
            ("reason", reason),
            ("net", net),
            ("tax", tax),
            ("total", net + tax),
            ("by", creator)).ConfigureAwait(false);
        var no = 0;
        foreach (var (state, lineNet, itbis) in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO sal.credit_note_line (credit_note_line_id, company_id, credit_note_id, line_no, invoice_line_id, net_amount, rate, itbis)
                VALUES (@id, @c, @n, @no, @il, @net, @rate, @itbis)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("n", context.ResultRef),
                ("no", ++no),
                ("il", state.InvoiceLineId),
                ("net", lineNet),
                ("rate", state.Rate),
                ("itbis", itbis)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Crediting.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { creditNoteId = context.ResultRef, creditNoteNo = number, commercialStatus = "DRAFT", netTotal = Crediting.M(net), taxTotal = Crediting.M(tax), total = Crediting.M(net + tax), version = 1 });
    }
}

[RequiresPermission("credit_note:issue", StepUp = true)]
public sealed class IssueCreditNoteHandler(EcfSwitch? gateway = null) : ICommandHandler<IssueCreditNote>
{
    private readonly PostingEngine _engine = new();
    private readonly EcfSwitch _gateway = gateway ?? EcfSwitch.Off;

    public string CommandType => "Sales.IssueCreditNote";

    private sealed record Note(Guid InvoiceId, string No, string Status, decimal Net, decimal Tax, decimal Total, long Version);

    public async Task<string> HandleAsync(IssueCreditNote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var invoiceId = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT invoice_id FROM sal.credit_note WHERE company_id = @c AND credit_note_id = @n", cancellationToken, ("c", context.CompanyId), ("n", command.CreditNoteId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The credit note does not exist.");
        var invoice = await Crediting.LockInvoiceAsync(context, invoiceId, cancellationToken).ConfigureAwait(false); // serializes an invoice's credits
        var note = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT invoice_id, credit_note_no, commercial_status, net_total::numeric(19,2), tax_total::numeric(19,2), total::numeric(19,2), version FROM sal.credit_note WHERE credit_note_id = @n FOR UPDATE",
            r => new Note(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetInt64(6)),
            cancellationToken,
            ("n", command.CreditNoteId)).ConfigureAwait(false))!;
        if (note.Version != command.ExpectedVersion)
        {
            throw new DomainException(SalesErrors.VersionConflict, $"The credit note changed (version {note.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (note.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The credit note is {note.Status}.");
        }

        Crediting.EnsureCreditable(invoice);
        var issuer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (issuer == invoice.IssuedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(SalesErrors.FourEyes, "A credit note is issued by someone other than who issued the invoice (VS#3 §7).");
        }

        // The invoice lines may have been credited by another note since this one was prepared.
        var states = await Crediting.LinesAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
        var lines = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT invoice_line_id, net_amount::numeric(19,2) FROM sal.credit_note_line WHERE credit_note_id = @n", r => (r.GetGuid(0), r.GetDecimal(1)), cancellationToken,
            ("n", command.CreditNoteId)).ConfigureAwait(false);
        if (lines.Any(l => l.Item2 > states.Single(s => s.InvoiceLineId == l.Item1).RemainingNet))
        {
            throw new DomainException(CreditNoteErrors.ExceedsCreditable, "Another credit note took part of these invoice lines; prepare this one again.");
        }

        var open = await SalesSql.ScalarAsync<decimal?>(context, "SELECT open_amount FROM fin.ar_document WHERE ar_doc_id = @a FOR UPDATE", cancellationToken, ("a", invoice.ArDocId!.Value)).ConfigureAwait(false) ?? 0m;
        if (note.Total > open)
        {
            throw new DomainException(CreditNoteErrors.ExceedsOpenReceivable, $"The note ({Crediting.M(note.Total)}) exceeds the invoice's open receivable ({Crediting.M(open)}); credit balances and refunds are out of VS#3 (E-VS3-06-3).");
        }

        var today = SalesSql.Today(context);
        // E-VS4-03-1: through Alanube when the gateway is on and e-CF 34 has an ACTIVE range; else the manual channel.
        var viaGateway = await EcfQueue.UsesGatewayAsync(context, _gateway, SalesEcf.CreditNoteType, cancellationToken).ConfigureAwait(false);
        var fiscal = viaGateway ? "ECF_SENDING" : "PENDING_EXTERNAL";
        var inputs = new Dictionary<string, string> { ["credit_note_no"] = note.No, ["invoice_no"] = invoice.InvoiceNo, ["invoice_encf"] = invoice.Encf! };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                Crediting.RuleCode,
                today,
                context.Clock.UtcNow,
                [
                    new PostingLineInput("P22-DR-DISC", "note_net", note.Net, PartyId: invoice.PartyId, Inputs: inputs),
                    new PostingLineInput("P22-DR-ITBIS", "note_itbis", note.Tax, Inputs: inputs),
                    new PostingLineInput("P22-CR-AR", "note_total", note.Total, PartyId: invoice.PartyId, SubledgerRef: invoice.ArDocId, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        var version = note.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CreditNoteIssued",
                1,
                Crediting.Aggregate,
                command.CreditNoteId,
                version,
                JsonSerializer.Serialize(new { creditNoteId = command.CreditNoteId, creditNoteNo = note.No, invoiceId, invoiceNo = invoice.InvoiceNo, invoiceEncf = invoice.Encf, total = Crediting.M(note.Total) }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.ar_document SET open_amount = open_amount - @t, version = version + 1 WHERE ar_doc_id = @a", cancellationToken,
            ("t", note.Total), ("a", invoice.ArDocId.Value)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE sal.credit_note SET commercial_status = 'CONFIRMED', accounting_status = 'POSTED', fiscal_status = @fiscal, credit_date = @d, posting_event_id = @e,
              issued_by = @by, version = @v
            WHERE credit_note_id = @n
            """,
            cancellationToken,
            ("fiscal", fiscal),
            ("d", today),
            ("e", eventId),
            ("by", issuer),
            ("v", version),
            ("n", command.CreditNoteId)).ConfigureAwait(false);
        await context.AppendStateAsync(Crediting.Aggregate, command.CreditNoteId, "DOCUMENT", "DRAFT", "CONFIRMED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Crediting.Aggregate, command.CreditNoteId, "ACCOUNTING", "NOT_POSTED", "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);

        if (await SalesSql.ScalarAsync<Guid?>(context, "SELECT fiscal_authorization_id FROM sal.invoice WHERE invoice_id = @i", cancellationToken, ("i", invoiceId)).ConfigureAwait(false) is { } authorization)
        {
            // E-FIS1-03-7 (D-11): a credit note of an exempt invoice returns the net it credits (a price credit has no quantity).
            await AuthorizationUsage.ReleaseAsync(context, authorization, [.. lines.Select(l => new ReleasedLine(l.Item1, 0m, l.Item2))], eventId, CommandType, cancellationToken).ConfigureAwait(false);
        }

        // E-VS3-06-4 / E-VS3-07-11: fully credited → CREDITED; a partly paid invoice the note closes → PAID.
        var invoiceStatus = await InvoiceStanding.RefreshAsync(context, invoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);

        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        var queued = viaGateway ? await SalesEcf.QueueCreditNoteAsync(context, command.CreditNoteId, today, CommandType, cancellationToken).ConfigureAwait(false) : null;
        return JsonSerializer.Serialize(new
        {
            creditNoteId = command.CreditNoteId,
            commercialStatus = "CONFIRMED",
            accountingStatus = "POSTED",
            fiscalStatus = fiscal,
            ecfNumber = queued?.Encf,
            invoiceStatus,
            journalId = journal.JournalId,
            version,
        });
    }
}

[RequiresPermission("fiscal_document:record")]
public sealed class RecordExternalCreditNoteDocumentHandler : ICommandHandler<RecordExternalCreditNoteDocument>
{
    public string CommandType => "Sales.RecordExternalCreditNoteDocument";

    private sealed record Note(Guid InvoiceId, Guid PartyId, string No, string Commercial, string Fiscal, decimal Net, decimal Tax, decimal Total, long Version);

    public async Task<string> HandleAsync(RecordExternalCreditNoteDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var note = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT invoice_id, party_id, credit_note_no, commercial_status, fiscal_status, net_total::numeric(19,2), tax_total::numeric(19,2), total::numeric(19,2), version
            FROM sal.credit_note WHERE company_id = @c AND credit_note_id = @n FOR UPDATE
            """,
            r => new Note(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("n", command.CreditNoteId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The credit note does not exist.");
        if (note.Version != command.ExpectedVersion)
        {
            throw new DomainException(SalesErrors.VersionConflict, $"The credit note changed (version {note.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (note.Commercial != "CONFIRMED" || note.Fiscal != "PENDING_EXTERNAL")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The credit note is {note.Commercial} / {note.Fiscal}.");
        }

        var encf = (command.Encf ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(encf, "^E34[0-9]{10}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            throw new DomainException(InvoiceErrors.EncfInvalid, "The e-NCF of a credit note is E34 followed by 10 digits (E-VS3-06-5).");
        }

        var security = SalesSql.Optional(command.SecurityCode, 60, "The security code") ?? throw new DomainException(InvoiceErrors.FiscalDocumentMismatch, "The security code is required.");
        var evidence = SalesSql.Optional(command.EvidenceRef, 200, "The evidence reference") ?? throw new DomainException(DeliveryErrors.EvidenceInvalid, "The XML or PDF reference is required.");
        var hash = Deliveries.Deliveries.Sha256(command.EvidenceSha256);
        if (command.IssuedAt > context.Clock.UtcNow)
        {
            throw new DomainException(InvoiceErrors.FiscalDocumentMismatch, "The e-CF cannot be issued in the future.");
        }

        // E-CF1-8: the e-CF 34 names the receiver of the invoice it credits (the final consumer's buyer, or nobody).
        var credited = await SalesSql.ScalarAsync<Guid?>(context, "SELECT invoice_id FROM sal.credit_note WHERE credit_note_id = @n", cancellationToken, ("n", command.CreditNoteId)).ConfigureAwait(false);
        var expected = await Invoices.FiscalReceiver.ExpectedAsync(context, credited!.Value, cancellationToken).ConfigureAwait(false);
        var (receiver, passport) = Invoices.FiscalReceiver.Given(command.ReceiverRnc, command.ReceiverPassport);
        var differences = new List<string>();
        differences.AddRange(Invoices.FiscalReceiver.Differences(expected, receiver, passport));

        if (command.NetTotal != note.Net)
        {
            differences.Add($"net {Crediting.M(command.NetTotal)} ≠ {Crediting.M(note.Net)}");
        }

        if (command.TaxTotal != note.Tax)
        {
            differences.Add($"ITBIS {Crediting.M(command.TaxTotal)} ≠ {Crediting.M(note.Tax)}");
        }

        if (command.Total != note.Total)
        {
            differences.Add($"total {Crediting.M(command.Total)} ≠ {Crediting.M(note.Total)}");
        }

        if (differences.Count > 0)
        {
            throw new DomainException(InvoiceErrors.FiscalDocumentMismatch, $"The e-CF does not match the credit note ({string.Join("; ", differences)}); it stays PENDING_EXTERNAL.");
        }

        if (await SalesSql.ScalarAsync<Guid?>(context, "SELECT credit_note_id FROM sal.credit_note WHERE company_id = @c AND encf = @e", cancellationToken, ("c", context.CompanyId), ("e", encf)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(InvoiceErrors.EncfDuplicate, $"e-NCF {encf} is already recorded.");
        }

        var recorder = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = note.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ExternalFiscalDocumentIssued",
                1,
                Crediting.Aggregate,
                command.CreditNoteId,
                version,
                JsonSerializer.Serialize(new { creditNoteId = command.CreditNoteId, creditNoteNo = note.No, encf, issuedAt = command.IssuedAt, evidenceRef = evidence, total = Crediting.M(note.Total) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        var recordId = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.external_fiscal_record (record_id, company_id, credit_note_id, encf, issued_at, security_code, evidence_ref, evidence_sha256, receiver_rnc, net_total, tax_total, total, recorded_by, event_id,
                                                    receiver_passport)
            VALUES (@id, @c, @n, @e, @at, @sec, @ref, @hash, @rnc, @net, @tax, @total, @by, @ev, @passport)
            """,
            cancellationToken,
            ("id", recordId),
            ("c", context.CompanyId),
            ("n", command.CreditNoteId),
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
            context.Connection, context.Transaction, "UPDATE sal.credit_note SET fiscal_status = 'ACCEPTED_EXTERNAL', encf = @e, version = @v WHERE credit_note_id = @n", cancellationToken,
            ("e", encf), ("v", version), ("n", command.CreditNoteId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, link_type, event_id)
            VALUES (@id, @c, 'ExternalFiscalRecord', @r, 'CreditNote', @n, 'FISCALIZES', @e)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("r", recordId),
            ("n", command.CreditNoteId),
            ("e", eventId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { creditNoteId = command.CreditNoteId, fiscalStatus = "ACCEPTED_EXTERNAL", encf, version });
    }
}
