using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Invoices;
using Rochell.Sales.Proformas;

namespace Rochell.Sales.Receipts;

[RequiresPermission("receipt:bounce", StepUp = true)]
public sealed class MarkReceiptBouncedHandler : ICommandHandler<MarkReceiptBounced>
{
    public const string RuleCode = "P-24";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.MarkReceiptBounced";

    public async Task<string> HandleAsync(MarkReceiptBounced command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);

        // Lock order: the proformas its live allocations touch → the invoices its live applications touch → AR documents → receipt;
        // an application or allocation added meanwhile is refused.
        // E-CF1-02-2: the cash orders it is assigned to come first of all (lock order: sales order → … → receipt).
        var assignedBefore = await CashSales.CashSaleStore.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false);
        await CashSales.CashSaleStore.LockOrdersAsync(context, assignedBefore.Select(a => a.SalesOrderId), cancellationToken).ConfigureAwait(false);
        var allocatedBefore = await Allocations.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false);
        await Allocations.LockProformasAsync(context, allocatedBefore.Select(a => a.ProformaId), cancellationToken).ConfigureAwait(false);
        var before = await Receipting.LiveApplicationsAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false);
        var (invoices, _) = await Receipting.LockInvoicesAsync(context, before.Select(a => a.InvoiceId), cancellationToken).ConfigureAwait(false);
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var applications = await Receipting.LiveApplicationsAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false);
        var allocations = await Allocations.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false);
        var assigned = await CashSales.CashSaleStore.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (!applications.Select(a => a.ApplicationId).Order().SequenceEqual(before.Select(a => a.ApplicationId).Order())
            || !allocations.Select(a => a.AllocationId).Order().SequenceEqual(allocatedBefore.Select(a => a.AllocationId).Order())
            || !assigned.Select(a => a.AllocationId).Order().SequenceEqual(assignedBefore.Select(a => a.AllocationId).Order()))
        {
            throw new DomainException(SalesErrors.VersionConflict, "The receipt's applications or allocations changed; reload and retry.");
        }

        if (receipt.Method != "CHEQUE" || receipt.Status != "RECORDED" || receipt.DepositId is null)
        {
            throw new DomainException(ReceiptErrors.NotBounceable, $"Only a deposited cheque bounces ({receipt.No} is {receipt.Method}, {receipt.Status}, {receipt.Bank}, E-VS3-07-8).");
        }

        // E-FIS1b-01-9: money already refunded to the customer (or about to be) is not undone by a bounce.
        if (await SalesSql.ScalarAsync<bool>(
                context, "SELECT EXISTS (SELECT 1 FROM fin.customer_refund WHERE company_id = @c AND receipt_id = @r AND status <> 'VOIDED')", cancellationToken,
                ("c", context.CompanyId), ("r", command.ReceiptId)).ConfigureAwait(false))
        {
            throw new DomainException(ReceiptErrors.NotBounceable, $"{receipt.No} has a customer refund; void it (or resolve it apart) before the cheque is marked bounced.");
        }

        var depositNo = (await SalesSql.ScalarAsync<string>(
            context, "SELECT deposit_no FROM fin.receipt_deposit WHERE deposit_id = @d", cancellationToken, ("d", receipt.DepositId.Value)).ConfigureAwait(false))!;
        var bankAccount = (await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT bank_account_id FROM fin.receipt_deposit WHERE deposit_id = @d", cancellationToken, ("d", receipt.DepositId.Value)).ConfigureAwait(false))!.Value;
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT 1 FROM fin.bank_account WHERE bank_account_id = @b FOR SHARE", cancellationToken, ("b", bankAccount))
            .ConfigureAwait(false);

        var today = SalesSql.Today(context);
        var inputs = new Dictionary<string, string> { ["receipt_no"] = receipt.No, ["deposit_no"] = depositNo };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                RuleCode,
                today,
                context.Clock.UtcNow,
                [
                    new PostingLineInput("P24-DR-UNAP", "receipt_amount", receipt.Amount, PartyId: receipt.PartyId, SubledgerRef: command.ReceiptId, Inputs: inputs),
                    new PostingLineInput("P24-CR-BANK", "receipt_amount", receipt.Amount, SubledgerRef: bankAccount, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);

        // Each live application is undone first (its own ReceiptUnapplied event and reversal), then the bounce itself.
        var version = receipt.Version;
        var unapplied = receipt.Unapplied;
        var undone = new List<Guid>();

        // E-FIS1b-01-4: a bounced cheque collects nothing — its allocations to proformas are released first.
        foreach (var group in allocations.GroupBy(a => a.EventId))
        {
            await Allocations.ReleaseAsync(context, command.ReceiptId, receipt.No, group.ToList(), ++version, reason, CommandType, null, cancellationToken).ConfigureAwait(false);
        }

        // E-CF1-02-2: so are its assignments to cash orders; the order keeps its state and dispatches nothing more until it is covered.
        foreach (var group in assigned.GroupBy(a => a.EventId))
        {
            await CashSales.CashSaleStore.ReleaseAsync(context, command.ReceiptId, receipt.No, group.ToList(), ++version, reason, CommandType, cancellationToken).ConfigureAwait(false);
        }

        foreach (var group in applications.GroupBy(a => a.EventId))
        {
            var (amount, unapplyEvent) = await Receipting.UndoAsync(context, _engine, command.ReceiptId, receipt, group.ToList(), ++version, reason, null, cancellationToken).ConfigureAwait(false);
            unapplied += amount;
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE fin.receipt SET unapplied_amount = @u, application_status = @s, version = @v WHERE receipt_id = @r",
                cancellationToken,
                ("u", unapplied),
                ("s", Receipting.ApplicationStatus(receipt.Amount, unapplied)),
                ("v", version),
                ("r", command.ReceiptId)).ConfigureAwait(false);
            undone.Add(unapplyEvent);
        }

        version++;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptBounced",
                1,
                Receipting.Aggregate,
                command.ReceiptId,
                version,
                JsonSerializer.Serialize(new { receiptId = command.ReceiptId, receiptNo = receipt.No, depositNo, amount = Receipting.Money(receipt.Amount), unapplyEvents = undone, reason }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE fin.receipt SET status = 'BOUNCED', unapplied_amount = amount, application_status = 'UNAPPLIED', closing_event_id = @e, closing_reason = @reason, version = @v
            WHERE receipt_id = @r
            """,
            cancellationToken,
            ("e", eventId),
            ("reason", reason),
            ("v", version),
            ("r", command.ReceiptId)).ConfigureAwait(false);
        await context.AppendStateAsync(Receipting.Aggregate, command.ReceiptId, "DOCUMENT", "RECORDED", "BOUNCED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        var statuses = new Dictionary<string, string>();
        foreach (var invoice in invoices)
        {
            statuses[invoice.InvoiceNo] = await InvoiceStanding.RefreshAsync(context, invoice.InvoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { receiptId = command.ReceiptId, status = "BOUNCED", invoices = statuses, journalId = journal.JournalId, version });
    }
}

[RequiresPermission("receipt:reverse", StepUp = true)]
public sealed class ReverseReceiptHandler : ICommandHandler<ReverseReceipt>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.ReverseReceipt";

    public async Task<string> HandleAsync(ReverseReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var notInBank = receipt.Method == "TRANSFER" ? receipt.Bank == "DEPOSITED" : receipt.Bank == "IN_TRANSIT";
        if (receipt.Status != "RECORDED" || receipt.Application != "UNAPPLIED" || receipt.Allocated > 0m || !notInBank)
        {
            throw new DomainException(
                ReceiptErrors.NotReversible,
                $"Only a receipt with nothing applied or allocated, not deposited or matched, is reversed ({receipt.No} is {receipt.Status}, {receipt.Application}, {receipt.Bank}; E-VS3-07-9).");
        }

        var journal = (await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT journal_id FROM fin.gl_journal j WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("e", receipt.PostingEventId)).ConfigureAwait(false))!.Value;
        var today = SalesSql.Today(context);
        var plan = await _engine.PrepareReversalAsync(context, journal, today, cancellationToken).ConfigureAwait(false);
        var version = receipt.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptReversed",
                1,
                Receipting.Aggregate,
                command.ReceiptId,
                version,
                JsonSerializer.Serialize(new { receiptId = command.ReceiptId, receiptNo = receipt.No, amount = Receipting.Money(receipt.Amount), reason }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, context.Clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.receipt SET status = 'REVERSED', closing_event_id = @e, closing_reason = @reason, version = @v WHERE receipt_id = @r",
            cancellationToken,
            ("e", eventId),
            ("reason", reason),
            ("v", version),
            ("r", command.ReceiptId)).ConfigureAwait(false);
        await context.AppendStateAsync(Receipting.Aggregate, command.ReceiptId, "DOCUMENT", "RECORDED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { receiptId = command.ReceiptId, status = "REVERSED", journalId = reversal.JournalId, version });
    }
}
