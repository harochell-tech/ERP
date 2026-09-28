using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Invoices;

namespace Rochell.Sales.Receipts;

internal static class Withholdings
{
    public const string Aggregate = "CustomerWithholding";
}

[RequiresPermission("customer_withholding:record")]
public sealed class RecordCustomerWithholdingHandler : ICommandHandler<RecordCustomerWithholding>
{
    public const string RuleCode = "P-27";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.RecordCustomerWithholding";

    public async Task<string> HandleAsync(RecordCustomerWithholding command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var kind = (command.Kind ?? string.Empty).Trim().ToUpperInvariant();
        if (kind is not ("ITBIS" or "ISR"))
        {
            throw new DomainException(ReceiptErrors.WithholdingKindInvalid, "A customer withholding is of ITBIS or ISR (E-VS3-07-7).");
        }

        var amount = SalesSql.Positive(command.Amount, 2, "The amount");
        var certificate = SalesSql.Optional(command.CertificateNo, 60, "The certificate number") ?? throw new DomainException(SalesErrors.FieldRequired, "The certificate number is required.");
        var evidence = SalesSql.Optional(command.EvidenceRef, 200, "The evidence reference") ?? throw new DomainException(SalesErrors.FieldRequired, "The certificate's file reference is required.");
        var hash = Deliveries.Deliveries.Sha256(command.EvidenceSha256);

        var (invoices, open) = await Receipting.LockInvoicesAsync(context, [command.InvoiceId], cancellationToken).ConfigureAwait(false);
        var invoice = invoices[0];
        if (invoice.Commercial is not ("CONFIRMED" or "PARTIALLY_PAID"))
        {
            throw new DomainException(ReceiptErrors.InvoiceNotOpen, $"{invoice.InvoiceNo} is {invoice.Commercial}; withholdings go on CONFIRMED or PARTIALLY_PAID invoices.");
        }

        var today = SalesSql.Today(context);
        if (command.WithholdingDate > today || command.WithholdingDate < invoice.InvoiceDate)
        {
            throw new DomainException(ReceiptErrors.DateInvalid, "The withholding is dated between the invoice date and today.");
        }

        if (amount > open[invoice.ArDocId])
        {
            throw new DomainException(ReceiptErrors.WithholdingExceeds, $"{Receipting.Money(amount)} exceeds the {Receipting.Money(open[invoice.ArDocId])} open on {invoice.InvoiceNo}.");
        }

        if (kind == "ITBIS")
        {
            var withheld = await SalesSql.ScalarAsync<decimal?>(
                context, "SELECT sum(amount) FROM fin.customer_withholding WHERE ar_doc_id = @a AND kind = 'ITBIS' AND status = 'ACTIVE'", cancellationToken, ("a", invoice.ArDocId))
                .ConfigureAwait(false) ?? 0m;
            if (withheld + amount > invoice.TaxTotal)
            {
                throw new DomainException(ReceiptErrors.WithholdingExceeds, $"The ITBIS withheld would exceed the invoice's ITBIS ({Receipting.Money(invoice.TaxTotal)}).");
            }
        }

        if (await SalesSql.ScalarAsync<Guid?>(
                context, "SELECT withholding_id FROM fin.customer_withholding WHERE company_id = @c AND party_id = @p AND kind = @k AND certificate_no = @n", cancellationToken,
                ("c", context.CompanyId), ("p", invoice.PartyId), ("k", kind), ("n", certificate)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(ReceiptErrors.CertificateDuplicate, $"Certificate {certificate} is already recorded for this customer.");
        }

        var recorder = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var inputs = new Dictionary<string, string> { ["kind"] = kind, ["certificate_no"] = certificate, ["invoice_no"] = invoice.InvoiceNo };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                RuleCode,
                command.WithholdingDate,
                context.Clock.UtcNow,
                [
                    new PostingLineInput("P27-DR-WH", "withheld_amount", amount, PartyId: invoice.PartyId, Inputs: inputs),
                    new PostingLineInput("P27-CR-AR", "withheld_amount", amount, PartyId: invoice.PartyId, SubledgerRef: invoice.ArDocId, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "WithholdingByCustomer",
                1,
                Withholdings.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { withholdingId = context.ResultRef, invoiceId = command.InvoiceId, invoiceNo = invoice.InvoiceNo, kind, amount = Receipting.Money(amount), certificateNo = certificate }),
                Publish: true,
                BusinessDate: command.WithholdingDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.customer_withholding (withholding_id, company_id, party_id, invoice_id, ar_doc_id, kind, amount, withholding_date, certificate_no, evidence_ref,
              evidence_sha256, status, posting_event_id, recorded_by, version)
            VALUES (@id, @c, @p, @i, @a, @k, @amount, @d, @n, @ref, @hash, 'ACTIVE', @e, @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("p", invoice.PartyId),
            ("i", command.InvoiceId),
            ("a", invoice.ArDocId),
            ("k", kind),
            ("amount", amount),
            ("d", command.WithholdingDate),
            ("n", certificate),
            ("ref", evidence),
            ("hash", hash),
            ("e", eventId),
            ("by", recorder)).ConfigureAwait(false);
        await context.AppendStateAsync(Withholdings.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Receipting.MoveOpenAsync(context, invoice.ArDocId, -amount, cancellationToken).ConfigureAwait(false);
        var status = await InvoiceStanding.RefreshAsync(context, command.InvoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { withholdingId = context.ResultRef, status = "ACTIVE", invoiceStatus = status, journalId = journal.JournalId, version = 1 });
    }
}

[RequiresPermission("customer_withholding:reverse", StepUp = true)]
public sealed class ReverseCustomerWithholdingHandler : ICommandHandler<ReverseCustomerWithholding>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.ReverseCustomerWithholding";

    private sealed record Row(Guid InvoiceId, Guid ArDocId, decimal Amount, string Status, Guid PostingEventId, long Version);

    public async Task<string> HandleAsync(ReverseCustomerWithholding command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);
        var invoiceId = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT invoice_id FROM fin.customer_withholding WHERE company_id = @c AND withholding_id = @w", cancellationToken, ("c", context.CompanyId), ("w", command.WithholdingId))
            .ConfigureAwait(false) ?? throw new DomainException(SalesErrors.NotFound, "The withholding does not exist.");
        await Receipting.LockInvoicesAsync(context, [invoiceId], cancellationToken).ConfigureAwait(false);
        var row = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT invoice_id, ar_doc_id, amount::numeric(19,2), status, posting_event_id, version FROM fin.customer_withholding WHERE withholding_id = @w FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2), r.GetString(3), r.GetGuid(4), r.GetInt64(5)),
            cancellationToken,
            ("w", command.WithholdingId)).ConfigureAwait(false))!;
        if (row.Version != command.ExpectedVersion)
        {
            throw new DomainException(SalesErrors.VersionConflict, $"The withholding changed (version {row.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (row.Status != "ACTIVE")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The withholding is {row.Status}.");
        }

        var journal = (await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT journal_id FROM fin.gl_journal j WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("e", row.PostingEventId)).ConfigureAwait(false))!.Value;
        var today = SalesSql.Today(context);
        var plan = await _engine.PrepareReversalAsync(context, journal, today, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerWithholdingReversed",
                1,
                Withholdings.Aggregate,
                command.WithholdingId,
                version,
                JsonSerializer.Serialize(new { withholdingId = command.WithholdingId, invoiceId, amount = Receipting.Money(row.Amount), reason }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, context.Clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.customer_withholding SET status = 'REVERSED', reversal_event_id = @e, reversal_reason = @reason, version = @v WHERE withholding_id = @w",
            cancellationToken,
            ("e", eventId),
            ("reason", reason),
            ("v", version),
            ("w", command.WithholdingId)).ConfigureAwait(false);
        await context.AppendStateAsync(Withholdings.Aggregate, command.WithholdingId, "DOCUMENT", "ACTIVE", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await Receipting.MoveOpenAsync(context, row.ArDocId, row.Amount, cancellationToken).ConfigureAwait(false);
        var status = await InvoiceStanding.RefreshAsync(context, invoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { withholdingId = command.WithholdingId, status = "REVERSED", invoiceStatus = status, journalId = reversal.JournalId, version });
    }
}
