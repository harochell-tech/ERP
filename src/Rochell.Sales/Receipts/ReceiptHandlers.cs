using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;

namespace Rochell.Sales.Receipts;

[RequiresPermission("receipt:record")]
public sealed class RecordReceiptHandler : ICommandHandler<RecordReceipt>
{
    public const string RuleCode = "P-23";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.RecordReceipt";

    public async Task<string> HandleAsync(RecordReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var method = (command.Method ?? string.Empty).Trim().ToUpperInvariant();
        if (method is not ("TRANSFER" or "CHEQUE" or "CASH"))
        {
            throw new DomainException(ReceiptErrors.MethodInvalid, "A receipt is a TRANSFER, a CHEQUE or CASH (E-VS3-11).");
        }

        var amount = SalesSql.Positive(command.Amount, 2, "The amount");
        if (await Orders.Orders.CustomerStatusAsync(context, command.PartyId, cancellationToken).ConfigureAwait(false) != "ACTIVE")
        {
            throw new DomainException(ReceiptErrors.CustomerNotActive, "Receipts are recorded for an ACTIVE customer.");
        }

        var today = SalesSql.Today(context);
        var reference = SalesSql.Optional(command.Reference, 100, "The reference");
        var valueDate = today;
        Guid? bankAccount = null;
        string? chequeBank = null;
        string? chequeNo = null;
        DateOnly? chequeDate = null;
        if (method == "TRANSFER")
        {
            bankAccount = command.BankAccountId ?? throw new DomainException(ReceiptErrors.BankAccountInvalid, "A transfer names the bank account it arrived to.");
            valueDate = command.ValueDate ?? throw new DomainException(ReceiptErrors.DateInvalid, "A transfer has its value date.");
            if (valueDate > today)
            {
                throw new DomainException(ReceiptErrors.DateInvalid, "The value date of a transfer is today or earlier.");
            }

            if (await SalesSql.ScalarAsync<string>(
                    context, "SELECT status FROM fin.bank_account WHERE company_id = @c AND bank_account_id = @b FOR SHARE", cancellationToken, ("c", context.CompanyId), ("b", bankAccount.Value))
                    .ConfigureAwait(false) != "ACTIVE")
            {
                throw new DomainException(ReceiptErrors.BankAccountInvalid, "The bank account does not exist or is closed.");
            }
        }
        else if (method == "CHEQUE")
        {
            chequeBank = SalesSql.Optional(command.ChequeBank, 60, "The cheque's bank");
            chequeNo = SalesSql.Optional(command.ChequeNo, 30, "The cheque number");
            chequeDate = command.ChequeDate;
            if (chequeBank is null || chequeNo is null || chequeDate is null)
            {
                throw new DomainException(SalesErrors.FieldRequired, "A cheque has its bank, number and date.");
            }

            if (chequeDate > today)
            {
                throw new DomainException(ReceiptErrors.DateInvalid, "Post-dated cheques are out of VS#3 (E-VS3-07-2).");
            }
        }

        var recorder = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "receipt-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(receipt_no from 5)::int) FROM fin.receipt WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var number = "REC-" + (last + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var inputs = new Dictionary<string, string> { ["receipt_no"] = number };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                RuleCode,
                valueDate,
                context.Clock.UtcNow,
                [
                    method == "TRANSFER"
                        ? new PostingLineInput("P23-DR-BANK", "receipt_amount", amount, SubledgerRef: bankAccount, Inputs: inputs)
                        : new PostingLineInput("P23-DR-CIT", "receipt_amount", amount, PartyId: command.PartyId, SubledgerRef: context.ResultRef, Inputs: inputs),
                    new PostingLineInput("P23-CR-UNAP", "receipt_amount", amount, PartyId: command.PartyId, SubledgerRef: context.ResultRef, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptRecorded",
                1,
                Receipting.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { receiptId = context.ResultRef, receiptNo = number, partyId = command.PartyId, method, amount = Receipting.Money(amount), valueDate, bankAccountId = bankAccount }),
                Publish: true,
                BusinessDate: valueDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.receipt (receipt_id, company_id, receipt_no, party_id, method, amount, receipt_date, value_date, bank_account_id, reference, cheque_bank, cheque_no,
              cheque_date, status, application_status, bank_status, unapplied_amount, posting_event_id, recorded_by, version)
            VALUES (@id, @c, @no, @p, @m, @a, @today, @vd, @b, @ref, @cb, @cn, @cd, 'RECORDED', 'UNAPPLIED', @bank, @a, @e, @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", number),
            ("p", command.PartyId),
            ("m", method),
            ("a", amount),
            ("today", today),
            ("vd", valueDate),
            ("b", bankAccount),
            ("ref", reference),
            ("cb", chequeBank),
            ("cn", chequeNo),
            ("cd", chequeDate),
            ("bank", method == "TRANSFER" ? "DEPOSITED" : "IN_TRANSIT"),
            ("e", eventId),
            ("by", recorder)).ConfigureAwait(false);
        await context.AppendStateAsync(Receipting.Aggregate, context.ResultRef, "DOCUMENT", null, "RECORDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            receiptId = context.ResultRef,
            receiptNo = number,
            status = "RECORDED",
            applicationStatus = "UNAPPLIED",
            bankStatus = method == "TRANSFER" ? "DEPOSITED" : "IN_TRANSIT",
            amount = Receipting.Money(amount),
            journalId = journal.JournalId,
            version = 1,
        });
    }
}

[RequiresPermission("receipt:deposit")]
public sealed class DepositReceiptsHandler : ICommandHandler<DepositReceipts>
{
    public const string RuleCode = "P-29";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.DepositReceipts";

    public async Task<string> HandleAsync(DepositReceipts command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = command.ReceiptIds ?? [];
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new DomainException(SalesErrors.LinesRequired, "A deposit takes one or more receipts, each once.");
        }

        var receipts = new List<(Guid Id, Receipting.Row Row)>();
        foreach (var id in ids.Order())
        {
            var row = await Receipting.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
            if (row.Method == "TRANSFER" || row.Status != "RECORDED" || row.Bank != "IN_TRANSIT")
            {
                throw new DomainException(ReceiptErrors.NotDepositable, $"{row.No} is not a cheque or cash receipt in transit ({row.Method}, {row.Status}, {row.Bank}).");
            }

            receipts.Add((id, row));
        }

        if (await SalesSql.ScalarAsync<string>(
                context, "SELECT status FROM fin.bank_account WHERE company_id = @c AND bank_account_id = @b FOR SHARE", cancellationToken, ("c", context.CompanyId), ("b", command.BankAccountId))
                .ConfigureAwait(false) != "ACTIVE")
        {
            throw new DomainException(ReceiptErrors.BankAccountInvalid, "The bank account does not exist or is closed.");
        }

        var today = SalesSql.Today(context);
        var total = receipts.Sum(r => r.Row.Amount);
        var depositor = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "deposit-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(deposit_no from 5)::int) FROM fin.receipt_deposit WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var number = "DEP-" + (last + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var lines = new List<PostingLineInput>
        {
            new("P29-DR-BANK", "deposit_total", total, SubledgerRef: command.BankAccountId, Inputs: new Dictionary<string, string> { ["deposit_no"] = number }),
        };
        lines.AddRange(receipts.Select(r => new PostingLineInput(
            "P29-CR-CIT", "receipt_amount", r.Row.Amount, PartyId: r.Row.PartyId, SubledgerRef: r.Id, Inputs: new Dictionary<string, string> { ["deposit_no"] = number, ["receipt_no"] = r.Row.No })));
        var plan = await _engine.PrepareAsync(context, new PostingRequest(RuleCode, today, context.Clock.UtcNow, lines), cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CashInTransitDeposited",
                1,
                Receipting.DepositAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { depositId = context.ResultRef, depositNo = number, bankAccountId = command.BankAccountId, total = Receipting.Money(total), receipts = receipts.Select(r => r.Row.No) }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.receipt_deposit (deposit_id, company_id, deposit_no, bank_account_id, deposit_date, total, status, posting_event_id, deposited_by, version)
            VALUES (@id, @c, @no, @b, @d, @t, 'DEPOSITED', @e, @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", number),
            ("b", command.BankAccountId),
            ("d", today),
            ("t", total),
            ("e", eventId),
            ("by", depositor)).ConfigureAwait(false);
        foreach (var (id, _) in receipts)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE fin.receipt SET deposit_id = @d, bank_status = 'DEPOSITED', version = version + 1 WHERE receipt_id = @r", cancellationToken,
                ("d", context.ResultRef), ("r", id)).ConfigureAwait(false);
        }

        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { depositId = context.ResultRef, depositNo = number, total = Receipting.Money(total), status = "DEPOSITED", journalId = journal.JournalId, version = 1 });
    }
}

[RequiresPermission("receipt:apply")]
public sealed class ApplyReceiptHandler : ICommandHandler<ApplyReceipt>
{
    public const string RuleCode = "P-25";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.ApplyReceipt";

    public async Task<string> HandleAsync(ApplyReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var input = command.Applications ?? [];
        if (input.Count == 0 || input.Select(a => a.InvoiceId).Distinct().Count() != input.Count)
        {
            throw new DomainException(SalesErrors.LinesRequired, "An application names one or more invoices, each once.");
        }

        foreach (var a in input)
        {
            SalesSql.Positive(a.Amount, 2, "Each applied amount");
        }

        // Lock order: invoices → AR documents → receipt (VS#3 §5).
        var (invoices, open) = await Receipting.LockInvoicesAsync(context, input.Select(a => a.InvoiceId), cancellationToken).ConfigureAwait(false);
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (receipt.Status != "RECORDED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The receipt is {receipt.Status}.");
        }

        // E-FIS1b-01-4: what is allocated to proformas waits for their invoice and is not applied elsewhere.
        var total = input.Sum(a => a.Amount);
        if (total > receipt.Unapplied - receipt.Allocated)
        {
            throw new DomainException(
                ReceiptErrors.ExceedsUnapplied,
                $"{Receipting.Money(total)} exceeds the {Receipting.Money(receipt.Unapplied - receipt.Allocated)} still unapplied on {receipt.No}"
                + (receipt.Allocated > 0m ? $" ({Receipting.Money(receipt.Allocated)} is allocated to proformas)." : "."));
        }

        var targets = new List<Receipting.Target>();
        foreach (var a in input)
        {
            var invoice = invoices.Single(i => i.InvoiceId == a.InvoiceId);
            if (invoice.PartyId != receipt.PartyId)
            {
                throw new DomainException(ReceiptErrors.OtherCustomer, $"{invoice.InvoiceNo} is another customer's invoice.");
            }

            if (invoice.Commercial is not ("CONFIRMED" or "PARTIALLY_PAID"))
            {
                throw new DomainException(ReceiptErrors.InvoiceNotOpen, $"{invoice.InvoiceNo} is {invoice.Commercial}; receipts apply to CONFIRMED or PARTIALLY_PAID invoices.");
            }

            if (a.Amount > open[invoice.ArDocId])
            {
                throw new DomainException(ReceiptErrors.ExceedsOpen, $"{Receipting.Money(a.Amount)} exceeds the {Receipting.Money(open[invoice.ArDocId])} open on {invoice.InvoiceNo}.");
            }

            targets.Add(new Receipting.Target(invoice.InvoiceId, invoice.InvoiceNo, invoice.ArDocId, a.Amount));
        }

        var version = receipt.Version + 1;
        var (eventId, journalId, unapplied, status) = await Receipting.ApplyAsync(context, _engine, command.ReceiptId, receipt, version, targets, null, cancellationToken).ConfigureAwait(false);
        var statuses = new Dictionary<string, string>();
        foreach (var invoice in invoices)
        {
            statuses[invoice.InvoiceNo] = await InvoiceStanding.RefreshAsync(context, invoice.InvoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            receiptId = command.ReceiptId,
            applicationEventId = eventId,
            applicationStatus = status,
            unapplied = Receipting.Money(unapplied),
            invoices = statuses,
            journalId,
            version,
        });
    }
}

[RequiresPermission("receipt:apply")]
public sealed class UnapplyReceiptHandler : ICommandHandler<UnapplyReceipt>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.UnapplyReceipt";

    public async Task<string> HandleAsync(UnapplyReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);
        var candidates = (await Receipting.LiveApplicationsAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false))
            .Where(a => a.EventId == command.ApplicationEventId).ToList();
        if (candidates.Count == 0)
        {
            throw new DomainException(ReceiptErrors.ApplicationNotFound, "The receipt has no live application of that event.");
        }

        var (invoices, _) = await Receipting.LockInvoicesAsync(context, candidates.Select(a => a.InvoiceId), cancellationToken).ConfigureAwait(false);
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, null, cancellationToken).ConfigureAwait(false);
        var applications = (await Receipting.LiveApplicationsAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false))
            .Where(a => a.EventId == command.ApplicationEventId).ToList();
        if (applications.Count != candidates.Count || receipt.Status != "RECORDED")
        {
            throw new DomainException(ReceiptErrors.ApplicationNotFound, "The application was already undone.");
        }

        var version = receipt.Version + 1;
        var (amount, eventId) = await Receipting.UndoAsync(context, _engine, command.ReceiptId, receipt, applications, version, reason, null, cancellationToken).ConfigureAwait(false);
        var unapplied = receipt.Unapplied + amount;
        var status = Receipting.ApplicationStatus(receipt.Amount, unapplied);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.receipt SET unapplied_amount = @u, application_status = @s, version = @v WHERE receipt_id = @r",
            cancellationToken,
            ("u", unapplied),
            ("s", status),
            ("v", version),
            ("r", command.ReceiptId)).ConfigureAwait(false);
        var statuses = new Dictionary<string, string>();
        foreach (var invoice in invoices)
        {
            statuses[invoice.InvoiceNo] = await InvoiceStanding.RefreshAsync(context, invoice.InvoiceId, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { receiptId = command.ReceiptId, applicationStatus = status, unapplied = Receipting.Money(unapplied), invoices = statuses, version });
    }
}
