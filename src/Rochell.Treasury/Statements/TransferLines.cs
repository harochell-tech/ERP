using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Transfers;

namespace Rochell.Treasury.Statements;

/// <summary>
/// E-USD1-05b-3: a person confirms that an UNMATCHED line is a RELEASED transfer's side in that account — the origin's DEBIT for what
/// left it, the destination's CREDIT for what entered it, in the account's currency — dated within [value date, value date + 10 days].
/// The transfer's status does not change; each of its two lines is matched once.
/// </summary>
public sealed record MatchBankLineToTransfer(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LineId, long ExpectedLineVersion, Guid TransferId) : ICommand;

internal static class TransferLines
{
    public static async Task<Guid?> MatchedAsync(CommandContext context, Guid lineId, CancellationToken cancellationToken)
        => await PaymentRules.ScalarAsync<Guid?>(
            context, "SELECT matched_transfer_id FROM fin.bank_statement_line WHERE line_id = @id AND company_id = @c", cancellationToken, ("id", lineId), ("c", context.CompanyId))
            .ConfigureAwait(false);

    public static async Task<string> MoveAsync(
        CommandContext context, Guid lineId, BankLines.Line line, Guid transferId, string transferNo, bool match, string? reason, string commandType, CancellationToken cancellationToken)
    {
        var version = line.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                match ? "BankLineMatched" : "BankLineUnmatched", 1, BankLines.Aggregate, lineId, version,
                JsonSerializer.Serialize(new { lineId, statementId = line.StatementId, transferId, transferNo, kind = "TRANSFER", reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = @s, matched_transfer_id = @t, version = @v WHERE line_id = @id",
            cancellationToken,
            ("s", match ? "MATCHED" : "UNMATCHED"),
            ("t", match ? transferId : (Guid?)null),
            ("v", version),
            ("id", lineId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, lineId, "DOCUMENT", match ? "UNMATCHED" : "MATCHED", match ? "MATCHED" : "UNMATCHED", commandType, eventId, cancellationToken, reason)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId, lineVersion = version, transferId, status = match ? "MATCHED" : "UNMATCHED" });
    }

    /// <summary>UnmatchBankLine of a transfer's line: the line returns to UNMATCHED; the transfer does not change.</summary>
    public static async Task<string> UnmatchAsync(UnmatchBankLine command, CommandContext context, Guid transferId, string reason, string commandType, CancellationToken cancellationToken)
    {
        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (line.Status != "MATCHED" || await MatchedAsync(context, command.LineId, cancellationToken).ConfigureAwait(false) != transferId)
        {
            throw new DomainException(StatementErrors.LineNotMatched, $"The line is {line.Status}; only a MATCHED line is unmatched.");
        }

        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        var number = await PaymentRules.ScalarAsync<string>(context, "SELECT transfer_no FROM fin.bank_transfer WHERE transfer_id = @t", cancellationToken, ("t", transferId))
            .ConfigureAwait(false);
        return await MoveAsync(context, command.LineId, line, transferId, number!, match: false, reason, commandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("bank_line:match")]
public sealed class MatchBankLineToTransferHandler : ICommandHandler<MatchBankLineToTransfer>
{
    public string CommandType => "Treasury.MatchBankLineToTransfer";

    public async Task<string> HandleAsync(MatchBankLineToTransfer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // Lock order: transfer → statement line → bank account.
        var transfer = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT transfer_no, from_bank_account_id, to_bank_account_id, from_amount, to_amount, value_date, status FROM fin.bank_transfer WHERE company_id = @c AND transfer_id = @t FOR UPDATE",
            r => (Number: r.GetString(0), From: r.GetGuid(1), To: r.GetGuid(2), FromAmount: r.GetDecimal(3), ToAmount: r.GetDecimal(4), Date: r.Date(5), Status: r.GetString(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", command.TransferId)).ConfigureAwait(false)).SingleOrDefault();
        if (transfer == default)
        {
            throw new DomainException(TransferErrors.NotFound, "The transfer does not exist.");
        }

        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedLineVersion, cancellationToken).ConfigureAwait(false);
        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        if (line.Status != "UNMATCHED")
        {
            throw new DomainException(StatementErrors.LineNotUnmatched, $"The line is {line.Status}.");
        }

        if (transfer.Status != "RELEASED")
        {
            throw new DomainException(TransferErrors.InvalidState, $"The transfer is {transfer.Status}; only a RELEASED transfer is matched.");
        }

        var (expectedAccount, amount) = line.Direction == StatementFile.Debit ? (transfer.From, transfer.FromAmount) : (transfer.To, transfer.ToAmount);
        if (line.BankAccountId != expectedAccount)
        {
            throw new DomainException(StatementErrors.WrongBankAccount, "A transfer's DEBIT line is in its origin account and its CREDIT line in its destination account.");
        }

        if (line.Amount != amount)
        {
            throw new DomainException(StatementErrors.AmountDiffers, $"The line is {PaymentRules.Money(line.Amount)} and the transfer {PaymentRules.Money(amount)} in that account (E-USD1-05b-3).");
        }

        if (line.ValueDate < transfer.Date || line.ValueDate > transfer.Date.AddDays(MatchBankLineHandler.MatchWindowDays))
        {
            throw new DomainException(
                StatementErrors.DateOutsideWindow,
                $"The line is dated {BankLines.Date(line.ValueDate)}, outside {BankLines.Date(transfer.Date)}…{BankLines.Date(transfer.Date.AddDays(MatchBankLineHandler.MatchWindowDays))}.");
        }

        return await TransferLines.MoveAsync(context, command.LineId, line, command.TransferId, transfer.Number, match: true, null, CommandType, cancellationToken).ConfigureAwait(false);
    }
}
