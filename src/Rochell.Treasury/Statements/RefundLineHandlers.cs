using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Treasury.Payments;

namespace Rochell.Treasury.Statements;

/// <summary>
/// E-FIS1b-01-9: an UNMATCHED DEBIT line is the bank paying a RELEASED customer refund of the same account and amount, dated within
/// ten days of its release; the line becomes MATCHED and the refund CLEARED. Unmatching (UnmatchBankLine) returns both.
/// </summary>
public sealed record MatchBankLineToRefund(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LineId, long ExpectedLineVersion, Guid RefundId, long ExpectedVersion) : ICommand;

/// <summary>Statement lines of customer refunds. Lock order: refund → line → bank account.</summary>
internal static class RefundLines
{
    public const string Aggregate = "CustomerRefund";

    public sealed record Refund(string No, Guid BankAccountId, decimal Amount, DateOnly? Date, string Status, long Version);

    public static async Task<Refund> LockAsync(CommandContext context, Guid refundId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               "SELECT refund_no, bank_account_id, amount, refund_date, status, version FROM fin.customer_refund WHERE company_id = @c AND refund_id = @r FOR UPDATE",
               r => new Refund(r.GetString(0), r.GetGuid(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.Date(3), r.GetString(4), r.GetInt64(5)),
               cancellationToken,
               ("c", context.CompanyId),
               ("r", refundId)).ConfigureAwait(false)
           ?? throw new DomainException(StatementErrors.NotFound, "The refund does not exist.");

    public static async Task<Guid?> MatchedAsync(CommandContext context, Guid lineId, CancellationToken cancellationToken)
        => await PaymentRules.ScalarAsync<Guid?>(
            context, "SELECT matched_refund_id FROM fin.bank_statement_line WHERE line_id = @id AND company_id = @c", cancellationToken, ("id", lineId), ("c", context.CompanyId)).ConfigureAwait(false);

    /// <summary>Moves the line and its refund together (MATCHED / CLEARED, or UNMATCHED / RELEASED), each with its event and state history.</summary>
    public static async Task<string> MoveAsync(
        CommandContext context, Guid lineId, BankLines.Line line, Guid refundId, Refund refund, bool match, string? reason, string commandType, CancellationToken cancellationToken)
    {
        var lineVersion = line.Version + 1;
        var refundVersion = refund.Version + 1;
        var lineEvent = await context.AppendEventAsync(
            new EventDraft(
                match ? "BankLineMatched" : "BankLineUnmatched",
                1,
                BankLines.Aggregate,
                lineId,
                lineVersion,
                JsonSerializer.Serialize(new { lineId, statementId = line.StatementId, refundId, refundNo = refund.No, kind = "REFUND", amount = PaymentRules.Money(line.Amount), reason }),
                Publish: true,
                BusinessDate: line.ValueDate),
            cancellationToken).ConfigureAwait(false);
        var refundEvent = await context.AppendEventAsync(
            new EventDraft(
                match ? "CustomerRefundCleared" : "CustomerRefundUncleared",
                1,
                Aggregate,
                refundId,
                refundVersion,
                JsonSerializer.Serialize(new { refundId, refundNo = refund.No, lineId, reason }),
                Publish: true,
                BusinessDate: line.ValueDate,
                CausationId: lineEvent),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = @s, matched_refund_id = @r, version = @v WHERE line_id = @id",
            cancellationToken,
            ("s", match ? "MATCHED" : "UNMATCHED"),
            ("r", match ? refundId : null),
            ("v", lineVersion),
            ("id", lineId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.customer_refund SET status = @s, version = @v WHERE refund_id = @id", cancellationToken,
            ("s", match ? "CLEARED" : "RELEASED"), ("v", refundVersion), ("id", refundId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, lineId, "DOCUMENT", match ? "UNMATCHED" : "MATCHED", match ? "MATCHED" : "UNMATCHED", commandType, lineEvent, cancellationToken, reason)
            .ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, refundId, "DOCUMENT", match ? "RELEASED" : "CLEARED", match ? "CLEARED" : "RELEASED", commandType, refundEvent, cancellationToken, reason)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId, lineVersion, refundId, refundStatus = match ? "CLEARED" : "RELEASED", refundVersion });
    }

    /// <summary>UnmatchBankLine of a refund's line: the refund returns to RELEASED.</summary>
    public static async Task<string> UnmatchAsync(UnmatchBankLine command, CommandContext context, Guid refundId, string reason, string commandType, CancellationToken cancellationToken)
    {
        var refund = await LockAsync(context, refundId, cancellationToken).ConfigureAwait(false);
        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (line.Status != "MATCHED" || await MatchedAsync(context, command.LineId, cancellationToken).ConfigureAwait(false) != refundId || refund.Status != "CLEARED")
        {
            throw new DomainException(StatementErrors.LineNotMatched, $"The line is {line.Status}; only a MATCHED line is unmatched.");
        }

        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        return await MoveAsync(context, command.LineId, line, refundId, refund, match: false, reason, commandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("bank_line:match")]
public sealed class MatchBankLineToRefundHandler : ICommandHandler<MatchBankLineToRefund>
{
    public string CommandType => "Treasury.MatchBankLineToRefund";

    public async Task<string> HandleAsync(MatchBankLineToRefund command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var refund = await RefundLines.LockAsync(context, command.RefundId, cancellationToken).ConfigureAwait(false);
        if (refund.Version != command.ExpectedVersion)
        {
            throw new DomainException(StatementErrors.VersionConflict, $"The refund is at version {refund.Version}, not {command.ExpectedVersion}.");
        }

        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedLineVersion, cancellationToken).ConfigureAwait(false);
        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        await BankLines.RequirePesoAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        if (refund.Status != "RELEASED" || refund.Date is not { } released)
        {
            throw new DomainException(StatementErrors.RefundNotMatchable, $"{refund.No} is {refund.Status}; a line matches a RELEASED refund.");
        }

        if (line.Status != "UNMATCHED")
        {
            throw new DomainException(StatementErrors.LineNotUnmatched, $"The line is {line.Status}.");
        }

        if (line.Direction != StatementFile.Debit)
        {
            throw new DomainException(StatementErrors.LineNotDebit, "A refund leaves the bank: it is matched by a DEBIT line.");
        }

        if (line.BankAccountId != refund.BankAccountId)
        {
            throw new DomainException(StatementErrors.WrongBankAccount, "The line and the refund belong to different bank accounts.");
        }

        if (line.Amount != refund.Amount)
        {
            throw new DomainException(StatementErrors.AmountDiffers, $"The line is {PaymentRules.Money(line.Amount)} and the refund {PaymentRules.Money(refund.Amount)}; the amounts must be equal.");
        }

        if (Math.Abs(line.ValueDate.DayNumber - released.DayNumber) > ReceiptLines.WindowDays)
        {
            throw new DomainException(
                StatementErrors.DateOutsideWindow,
                $"The line is dated {BankLines.Date(line.ValueDate)}, outside {BankLines.Date(released.AddDays(-ReceiptLines.WindowDays))}…{BankLines.Date(released.AddDays(ReceiptLines.WindowDays))}.");
        }

        return await RefundLines.MoveAsync(context, command.LineId, line, command.RefundId, refund, match: true, null, CommandType, cancellationToken).ConfigureAwait(false);
    }
}
