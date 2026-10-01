using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Receipts;

namespace Rochell.Sales.Refunds;

public static class RefundErrors
{
    public const string MethodInvalid = "REFUND_METHOD_INVALID";
    public const string ExceedsCreditBalance = "REFUND_EXCEEDS_CREDIT_BALANCE";
    public const string ReceiptNotRefundable = "RECEIPT_NOT_REFUNDABLE";
    public const string BankAccountInvalid = "BANK_ACCOUNT_INVALID";
}

/// <summary>
/// E-FIS1b-8, E-FIS1b-01-9: Cobros prepares the refund of a receipt's credit balance — what is neither applied nor allocated to
/// proformas — to be paid from one of the company's bank accounts by transfer or cheque. Nothing posts until it is released.
/// </summary>
public sealed record PrepareCustomerRefund(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, Guid BankAccountId, string Method, decimal Amount, string Reason, string? Reference = null) : ICommand;

/// <summary>Someone other than who prepared it releases the refund (step-up): the money leaves the bank (P-36) and the receipt's unapplied amount.</summary>
public sealed record ReleaseCustomerRefund(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RefundId, long ExpectedVersion) : ICommand;

/// <summary>A prepared refund that will not be paid is voided with a reason.</summary>
public sealed record VoidCustomerRefund(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RefundId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>Shared reads and locks. Lock order: refund → receipt → bank account.</summary>
internal static class Refunds
{
    public const string Aggregate = "CustomerRefund";
    public const string RuleCode = "P-36";

    public sealed record Row(string RefundNo, Guid PartyId, Guid ReceiptId, Guid BankAccountId, string Method, decimal Amount, string Status, Guid PreparedBy, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid refundId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT refund_no, party_id, receipt_id, bank_account_id, method, amount::numeric(19,2), status, prepared_by, version FROM fin.customer_refund WHERE company_id = @c AND refund_id = @r FOR UPDATE",
            r => new Row(r.GetString(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetDecimal(5), r.GetString(6), r.GetGuid(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", refundId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The refund does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(SalesErrors.VersionConflict, $"The refund changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    /// <summary>What other refunds of the receipt, prepared and not yet released or voided, already take.</summary>
    public static async Task<decimal> PreparedAsync(CommandContext context, Guid receiptId, Guid? except, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<decimal?>(
               context,
               "SELECT coalesce(sum(amount), 0)::numeric(19,2) FROM fin.customer_refund WHERE company_id = @c AND receipt_id = @r AND status = 'PREPARED' AND refund_id IS DISTINCT FROM CAST(@x AS uuid)",
               cancellationToken,
               ("c", context.CompanyId),
               ("r", receiptId),
               ("x", except)).ConfigureAwait(false) ?? 0m;

    public static async Task RequireActiveBankAccountAsync(CommandContext context, Guid bankAccountId, CancellationToken cancellationToken)
    {
        if (await SalesSql.ScalarAsync<string>(
                context, "SELECT status FROM fin.bank_account WHERE company_id = @c AND bank_account_id = @b FOR SHARE", cancellationToken,
                ("c", context.CompanyId), ("b", bankAccountId)).ConfigureAwait(false) != "ACTIVE")
        {
            throw new DomainException(RefundErrors.BankAccountInvalid, "The refund is paid from an ACTIVE bank account of the company.");
        }
    }

    /// <summary>The receipt's money must be in the bank and not be a bounced or reversed receipt.</summary>
    public static void RequireRefundable(Receipting.Row receipt)
    {
        if (receipt.Status != "RECORDED" || receipt.Bank == "IN_TRANSIT")
        {
            throw new DomainException(RefundErrors.ReceiptNotRefundable, $"{receipt.No} is {receipt.Status} / {receipt.Bank}: only a receipt whose money is in the bank is refunded.");
        }
    }
}

[RequiresPermission("customer_refund:prepare")]
public sealed class PrepareCustomerRefundHandler : ICommandHandler<PrepareCustomerRefund>
{
    public string CommandType => "Sales.PrepareCustomerRefund";

    public async Task<string> HandleAsync(PrepareCustomerRefund command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var method = (command.Method ?? string.Empty).Trim().ToUpperInvariant();
        if (method is not ("TRANSFER" or "CHEQUE"))
        {
            throw new DomainException(RefundErrors.MethodInvalid, "A refund is paid by TRANSFER or CHEQUE.");
        }

        var amount = SalesSql.Positive(command.Amount, 2, "The refund amount");
        var reason = Receipting.Reason(command.Reason);
        var reference = SalesSql.Optional(command.Reference, 100, "The reference");
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, null, cancellationToken).ConfigureAwait(false);
        Refunds.RequireRefundable(receipt);
        await Refunds.RequireActiveBankAccountAsync(context, command.BankAccountId, cancellationToken).ConfigureAwait(false);
        var balance = receipt.Unapplied - receipt.Allocated - await Refunds.PreparedAsync(context, command.ReceiptId, null, cancellationToken).ConfigureAwait(false);
        if (amount > balance)
        {
            throw new DomainException(
                RefundErrors.ExceedsCreditBalance, $"{Receipting.Money(amount)} exceeds the {Receipting.Money(balance)} of {receipt.No} that is neither applied, allocated nor in another prepared refund.");
        }

        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "refund-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(refund_no from 5)::int) FROM fin.customer_refund WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var refundNo = "DEV-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerRefundPrepared",
                1,
                Refunds.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    refundId = context.ResultRef,
                    refundNo,
                    partyId = receipt.PartyId,
                    receiptId = command.ReceiptId,
                    receiptNo = receipt.No,
                    bankAccountId = command.BankAccountId,
                    method,
                    amount = Receipting.Money(amount),
                    reason,
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.customer_refund (refund_id, company_id, refund_no, party_id, receipt_id, bank_account_id, method, amount, reference, reason, status, prepared_by, version)
            VALUES (@id, @c, @no, @p, @r, @b, @m, @a, @ref, @reason, 'PREPARED', @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", refundNo),
            ("p", receipt.PartyId),
            ("r", command.ReceiptId),
            ("b", command.BankAccountId),
            ("m", method),
            ("a", amount),
            ("ref", reference),
            ("reason", reason),
            ("by", preparer)).ConfigureAwait(false);
        await context.AppendStateAsync(Refunds.Aggregate, context.ResultRef, "DOCUMENT", null, "PREPARED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { refundId = context.ResultRef, refundNo, status = "PREPARED", amount = Receipting.Money(amount), version = 1 });
    }
}

[RequiresPermission("customer_refund:release", StepUp = true)]
public sealed class ReleaseCustomerRefundHandler : ICommandHandler<ReleaseCustomerRefund>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Sales.ReleaseCustomerRefund";

    public async Task<string> HandleAsync(ReleaseCustomerRefund command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var refund = await Refunds.LockAsync(context, command.RefundId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (refund.Status != "PREPARED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The refund is {refund.Status}.");
        }

        var releaser = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (releaser == refund.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(SalesErrors.FourEyes, "A refund is released by someone other than who prepared it (E-FIS1b-8).");
        }

        var receipt = await Receipting.LockAsync(context, refund.ReceiptId, null, cancellationToken).ConfigureAwait(false);
        Refunds.RequireRefundable(receipt);
        await Refunds.RequireActiveBankAccountAsync(context, refund.BankAccountId, cancellationToken).ConfigureAwait(false);
        if (refund.Amount > receipt.Unapplied - receipt.Allocated)
        {
            throw new DomainException(
                RefundErrors.ExceedsCreditBalance, $"{Receipting.Money(refund.Amount)} exceeds the {Receipting.Money(receipt.Unapplied - receipt.Allocated)} of {receipt.No} that is neither applied nor allocated.");
        }

        var today = SalesSql.Today(context);
        var inputs = new Dictionary<string, string> { ["refund_no"] = refund.RefundNo, ["receipt_no"] = receipt.No };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                Refunds.RuleCode,
                today,
                context.Clock.UtcNow,
                [
                    new PostingLineInput("P36-DR-UNAP", "refund_amount", refund.Amount, PartyId: refund.PartyId, SubledgerRef: refund.ReceiptId, Inputs: inputs),
                    new PostingLineInput("P36-CR-BANK", "refund_amount", refund.Amount, SubledgerRef: refund.BankAccountId, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        var version = refund.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerRefundReleased",
                1,
                Refunds.Aggregate,
                command.RefundId,
                version,
                JsonSerializer.Serialize(new { refundId = command.RefundId, refundNo = refund.RefundNo, receiptId = refund.ReceiptId, receiptNo = receipt.No, amount = Receipting.Money(refund.Amount), refundDate = today }),
                Publish: true,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        var unapplied = receipt.Unapplied - refund.Amount;
        var receiptVersion = receipt.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "ReceiptRefunded",
                1,
                Receipting.Aggregate,
                refund.ReceiptId,
                receiptVersion,
                JsonSerializer.Serialize(new { receiptId = refund.ReceiptId, receiptNo = receipt.No, refundId = command.RefundId, refundNo = refund.RefundNo, amount = Receipting.Money(refund.Amount) }),
                Publish: true,
                BusinessDate: today,
                CausationId: eventId),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.receipt SET unapplied_amount = @u, application_status = @s, version = @v WHERE receipt_id = @r",
            cancellationToken,
            ("u", unapplied),
            ("s", Receipting.ApplicationStatus(receipt.Amount, unapplied)),
            ("v", receiptVersion),
            ("r", refund.ReceiptId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.customer_refund SET status = 'RELEASED', released_by = @by, posting_event_id = @e, refund_date = @d, version = @v WHERE refund_id = @id",
            cancellationToken,
            ("by", releaser),
            ("e", eventId),
            ("d", today),
            ("v", version),
            ("id", command.RefundId)).ConfigureAwait(false);
        await context.AppendStateAsync(Refunds.Aggregate, command.RefundId, "DOCUMENT", "PREPARED", "RELEASED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            refundId = command.RefundId,
            refundNo = refund.RefundNo,
            status = "RELEASED",
            receiptUnapplied = Receipting.Money(unapplied),
            journalId = journal.JournalId,
            version,
        });
    }
}

[RequiresPermission("customer_refund:prepare")]
public sealed class VoidCustomerRefundHandler : ICommandHandler<VoidCustomerRefund>
{
    public string CommandType => "Sales.VoidCustomerRefund";

    public async Task<string> HandleAsync(VoidCustomerRefund command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);
        var refund = await Refunds.LockAsync(context, command.RefundId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (refund.Status != "PREPARED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The refund is {refund.Status}; only a PREPARED refund is voided.");
        }

        var version = refund.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("CustomerRefundVoided", 1, Refunds.Aggregate, command.RefundId, version, JsonSerializer.Serialize(new { refundId = command.RefundId, refundNo = refund.RefundNo, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.customer_refund SET status = 'VOIDED', void_reason = @r, version = @v WHERE refund_id = @id", cancellationToken,
            ("r", reason), ("v", version), ("id", command.RefundId)).ConfigureAwait(false);
        await context.AppendStateAsync(Refunds.Aggregate, command.RefundId, "DOCUMENT", "PREPARED", "VOIDED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { refundId = command.RefundId, status = "VOIDED", version });
    }
}
