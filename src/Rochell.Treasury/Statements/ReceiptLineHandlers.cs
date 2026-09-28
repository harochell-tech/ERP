using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Treasury.Payments;

namespace Rochell.Treasury.Statements;

/// <summary>Statement lines of customer receipts (E-VS3-07-10). Lock order: receipt or deposit (and its receipts) → line → bank account.</summary>
internal static class ReceiptLines
{
    public const string ReceiptAggregate = "Receipt";
    public const string DepositAggregate = "ReceiptDeposit";

    /// <summary>A transfer arrives within this many days around the value date the customer gave (E-VS3-07-10).</summary>
    public const int WindowDays = 10;

    public sealed record Receipt(string No, string Method, decimal Amount, DateOnly ValueDate, Guid? BankAccountId, string Status, string Bank, Guid? DepositId, long Version);

    public sealed record Deposit(string No, Guid BankAccountId, DateOnly Date, decimal Total, string Status, long Version);

    public static async Task<Receipt> LockReceiptAsync(CommandContext context, Guid receiptId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               "SELECT receipt_no, method, amount, value_date, bank_account_id, status, bank_status, deposit_id, version FROM fin.receipt WHERE company_id = @c AND receipt_id = @r FOR UPDATE",
               r => new Receipt(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.Date(3), r.NullableGuid(4), r.GetString(5), r.GetString(6), r.NullableGuid(7), r.GetInt64(8)),
               cancellationToken,
               ("c", context.CompanyId),
               ("r", receiptId)).ConfigureAwait(false)
           ?? throw new DomainException(StatementErrors.NotFound, "The receipt does not exist.");

    /// <summary>The deposit, then its receipts in id order.</summary>
    public static async Task<(Deposit Deposit, List<Guid> Receipts)> LockDepositAsync(CommandContext context, Guid depositId, CancellationToken cancellationToken)
    {
        var deposit = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT deposit_no, bank_account_id, deposit_date, total, status, version FROM fin.receipt_deposit WHERE company_id = @c AND deposit_id = @d FOR UPDATE",
            r => new Deposit(r.GetString(0), r.GetGuid(1), r.Date(2), r.GetDecimal(3), r.GetString(4), r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", depositId)).ConfigureAwait(false)
            ?? throw new DomainException(StatementErrors.NotFound, "The deposit does not exist.");
        var receipts = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT receipt_id FROM fin.receipt WHERE deposit_id = @d ORDER BY receipt_id FOR UPDATE", r => r.GetGuid(0), cancellationToken,
            ("d", depositId)).ConfigureAwait(false);
        return (deposit, receipts);
    }

    /// <summary>Moves a deposit and all its receipts to <paramref name="to"/> (DEPOSITED / MATCHED), each version + 1.</summary>
    public static async Task MoveDepositAsync(CommandContext context, Guid depositId, string to, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt_deposit SET status = @s, version = version + 1 WHERE deposit_id = @d", cancellationToken,
            ("s", to), ("d", depositId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt SET bank_status = @s, version = version + 1 WHERE deposit_id = @d", cancellationToken,
            ("s", to), ("d", depositId)).ConfigureAwait(false);
    }

    public static async Task<(Guid? Receipt, Guid? Deposit)> MatchedAsync(CommandContext context, Guid lineId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               "SELECT matched_receipt_id, matched_deposit_id FROM fin.bank_statement_line WHERE line_id = @id AND company_id = @c",
               r => new Tuple<Guid?, Guid?>(r.NullableGuid(0), r.NullableGuid(1)),
               cancellationToken,
               ("id", lineId),
               ("c", context.CompanyId)).ConfigureAwait(false) is { } t ? (t.Item1, t.Item2) : (null, null);

    public static Task SetLineAsync(CommandContext context, Guid lineId, string status, Guid? receiptId, Guid? depositId, long version, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = @s, matched_receipt_id = @r, matched_deposit_id = @d, version = @v WHERE line_id = @id",
            cancellationToken,
            ("s", status),
            ("r", receiptId),
            ("d", depositId),
            ("v", version),
            ("id", lineId));
}

[RequiresPermission("bank_line:match")]
public sealed class MatchBankLineToReceiptHandler : ICommandHandler<MatchBankLineToReceipt>
{
    public string CommandType => "Treasury.MatchBankLineToReceipt";

    public async Task<string> HandleAsync(MatchBankLineToReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if ((command.ReceiptId is null) == (command.DepositId is null))
        {
            throw new DomainException(StatementErrors.MatchTargetRequired, "Name a receipt or a deposit, not both.");
        }

        return command.ReceiptId is { } receiptId
            ? await MatchReceiptAsync(command, context, receiptId, cancellationToken).ConfigureAwait(false)
            : await MatchDepositAsync(command, context, command.DepositId!.Value, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSame(BankLines.Line line, Guid bankAccountId, decimal amount)
    {
        if (line.Status != "UNMATCHED")
        {
            throw new DomainException(StatementErrors.LineNotUnmatched, $"The line is {line.Status}.");
        }

        if (line.BankAccountId != bankAccountId)
        {
            throw new DomainException(StatementErrors.WrongBankAccount, "The line and the receipt belong to different bank accounts.");
        }

        if (line.Amount != amount)
        {
            throw new DomainException(StatementErrors.AmountDiffers, $"The line is {PaymentRules.Money(line.Amount)} and the receipt {PaymentRules.Money(amount)}; the amounts must be equal.");
        }
    }

    private async Task<string> MatchReceiptAsync(MatchBankLineToReceipt command, CommandContext context, Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await ReceiptLines.LockReceiptAsync(context, receiptId, cancellationToken).ConfigureAwait(false);
        if (receipt.Version != command.ExpectedVersion)
        {
            throw new DomainException(StatementErrors.VersionConflict, $"The receipt is at version {receipt.Version}, not {command.ExpectedVersion}.");
        }

        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedLineVersion, cancellationToken).ConfigureAwait(false);
        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        var lineVersion = line.Version + 1;
        if (line.Direction == StatementFile.Debit)
        {
            // The bank takes a bounced cheque back: the receipt is already BOUNCED (P-24); only the line changes.
            if (receipt.Status != "BOUNCED")
            {
                throw new DomainException(StatementErrors.ReceiptNotMatchable, $"{receipt.No} is {receipt.Status}; a DEBIT line matches a BOUNCED cheque only.");
            }

            var (depositNo, depositAccount, depositDate) = (await Reading.SingleOrDefaultAsync(
                context.Connection, context.Transaction, "SELECT deposit_no, bank_account_id, deposit_date FROM fin.receipt_deposit WHERE deposit_id = @d",
                r => new Tuple<string, Guid, DateOnly>(r.GetString(0), r.GetGuid(1), r.Date(2)), cancellationToken, ("d", receipt.DepositId!.Value)).ConfigureAwait(false))!;
            EnsureSame(line, depositAccount, receipt.Amount);
            if (line.ValueDate < depositDate)
            {
                throw new DomainException(StatementErrors.DateOutsideWindow, $"The line is dated {BankLines.Date(line.ValueDate)}, before the deposit on {BankLines.Date(depositDate)}.");
            }

            var bounceMatched = await context.AppendEventAsync(
                new EventDraft(
                    "BankLineMatched",
                    1,
                    BankLines.Aggregate,
                    command.LineId,
                    lineVersion,
                    JsonSerializer.Serialize(new { lineId = command.LineId, statementId = line.StatementId, receiptId, receiptNo = receipt.No, depositNo, kind = "BOUNCE", amount = PaymentRules.Money(line.Amount) }),
                    Publish: true,
                    BusinessDate: line.ValueDate),
                cancellationToken).ConfigureAwait(false);
            await ReceiptLines.SetLineAsync(context, command.LineId, "MATCHED", receiptId, null, lineVersion, cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "UNMATCHED", "MATCHED", CommandType, bounceMatched, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion, receiptId, receiptStatus = receipt.Status, receiptVersion = receipt.Version });
        }

        if (receipt.Method != "TRANSFER" || receipt.Status != "RECORDED" || receipt.Bank != "DEPOSITED")
        {
            throw new DomainException(StatementErrors.ReceiptNotMatchable, $"{receipt.No} is not a recorded transfer waiting for its line ({receipt.Method}, {receipt.Status}, {receipt.Bank}).");
        }

        EnsureSame(line, receipt.BankAccountId!.Value, receipt.Amount);
        if (Math.Abs(line.ValueDate.DayNumber - receipt.ValueDate.DayNumber) > ReceiptLines.WindowDays)
        {
            throw new DomainException(
                StatementErrors.DateOutsideWindow,
                $"The line is dated {BankLines.Date(line.ValueDate)}, outside {BankLines.Date(receipt.ValueDate.AddDays(-ReceiptLines.WindowDays))}…{BankLines.Date(receipt.ValueDate.AddDays(ReceiptLines.WindowDays))} (E-VS3-07-10).");
        }

        var receiptVersion = receipt.Version + 1;
        var matched = await context.AppendEventAsync(
            new EventDraft(
                "BankLineMatched",
                1,
                BankLines.Aggregate,
                command.LineId,
                lineVersion,
                JsonSerializer.Serialize(new { lineId = command.LineId, statementId = line.StatementId, receiptId, receiptNo = receipt.No, kind = "RECEIPT", amount = PaymentRules.Money(line.Amount) }),
                Publish: true,
                BusinessDate: line.ValueDate),
            cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft(
                "ReceiptMatched",
                1,
                ReceiptLines.ReceiptAggregate,
                receiptId,
                receiptVersion,
                JsonSerializer.Serialize(new { receiptId, receiptNo = receipt.No, lineId = command.LineId, matchedOn = line.ValueDate }),
                Publish: true,
                BusinessDate: line.ValueDate,
                CausationId: matched),
            cancellationToken).ConfigureAwait(false);
        await ReceiptLines.SetLineAsync(context, command.LineId, "MATCHED", receiptId, null, lineVersion, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt SET bank_status = 'MATCHED', version = @v WHERE receipt_id = @r", cancellationToken,
            ("v", receiptVersion), ("r", receiptId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "UNMATCHED", "MATCHED", CommandType, matched, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion, receiptId, bankStatus = "MATCHED", receiptVersion });
    }

    private async Task<string> MatchDepositAsync(MatchBankLineToReceipt command, CommandContext context, Guid depositId, CancellationToken cancellationToken)
    {
        var (deposit, _) = await ReceiptLines.LockDepositAsync(context, depositId, cancellationToken).ConfigureAwait(false);
        if (deposit.Version != command.ExpectedVersion)
        {
            throw new DomainException(StatementErrors.VersionConflict, $"The deposit is at version {deposit.Version}, not {command.ExpectedVersion}.");
        }

        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedLineVersion, cancellationToken).ConfigureAwait(false);
        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        if (line.Direction != StatementFile.Credit || deposit.Status != "DEPOSITED")
        {
            throw new DomainException(StatementErrors.ReceiptNotMatchable, $"A deposit waiting for its line ({deposit.No} is {deposit.Status}) matches a CREDIT line.");
        }

        EnsureSame(line, deposit.BankAccountId, deposit.Total);
        if (line.ValueDate < deposit.Date || line.ValueDate > deposit.Date.AddDays(ReceiptLines.WindowDays))
        {
            throw new DomainException(
                StatementErrors.DateOutsideWindow,
                $"The line is dated {BankLines.Date(line.ValueDate)}, outside {BankLines.Date(deposit.Date)}…{BankLines.Date(deposit.Date.AddDays(ReceiptLines.WindowDays))} (E-VS3-07-10).");
        }

        var lineVersion = line.Version + 1;
        var matched = await context.AppendEventAsync(
            new EventDraft(
                "BankLineMatched",
                1,
                BankLines.Aggregate,
                command.LineId,
                lineVersion,
                JsonSerializer.Serialize(new { lineId = command.LineId, statementId = line.StatementId, depositId, depositNo = deposit.No, kind = "DEPOSIT", amount = PaymentRules.Money(line.Amount) }),
                Publish: true,
                BusinessDate: line.ValueDate),
            cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft(
                "DepositMatched",
                1,
                ReceiptLines.DepositAggregate,
                depositId,
                deposit.Version + 1,
                JsonSerializer.Serialize(new { depositId, depositNo = deposit.No, lineId = command.LineId, matchedOn = line.ValueDate }),
                Publish: true,
                BusinessDate: line.ValueDate,
                CausationId: matched),
            cancellationToken).ConfigureAwait(false);
        await ReceiptLines.SetLineAsync(context, command.LineId, "MATCHED", null, depositId, lineVersion, cancellationToken).ConfigureAwait(false);
        await ReceiptLines.MoveDepositAsync(context, depositId, "MATCHED", cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "UNMATCHED", "MATCHED", CommandType, matched, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion, depositId, depositStatus = "MATCHED", depositVersion = deposit.Version + 1 });
    }
}

/// <summary>UnmatchBankLine for a receipt's or deposit's line (called by <see cref="UnmatchBankLineHandler"/>).</summary>
internal static class ReceiptUnmatching
{
    public static async Task<string> UnmatchAsync(UnmatchBankLine command, CommandContext context, Guid? receiptId, Guid? depositId, string reason, string commandType, CancellationToken cancellationToken)
    {
        ReceiptLines.Receipt? receipt = null;
        if (receiptId is { } r)
        {
            receipt = await ReceiptLines.LockReceiptAsync(context, r, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReceiptLines.LockDepositAsync(context, depositId!.Value, cancellationToken).ConfigureAwait(false);
        }

        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var now = await ReceiptLines.MatchedAsync(context, command.LineId, cancellationToken).ConfigureAwait(false);
        if (line.Status != "MATCHED" || now != (receiptId, depositId))
        {
            throw new DomainException(StatementErrors.LineNotMatched, $"The line is {line.Status}; only a MATCHED line is unmatched.");
        }

        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        var version = line.Version + 1;
        var kind = depositId is not null ? "DEPOSIT" : line.Direction == StatementFile.Debit ? "BOUNCE" : "RECEIPT";
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankLineUnmatched",
                1,
                BankLines.Aggregate,
                command.LineId,
                version,
                JsonSerializer.Serialize(new { lineId = command.LineId, statementId = line.StatementId, receiptId, depositId, kind, reason }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await ReceiptLines.SetLineAsync(context, command.LineId, "UNMATCHED", null, null, version, cancellationToken).ConfigureAwait(false);
        if (kind == "RECEIPT")
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE fin.receipt SET bank_status = 'DEPOSITED', version = @v WHERE receipt_id = @r", cancellationToken,
                ("v", receipt!.Version + 1), ("r", receiptId)).ConfigureAwait(false);
        }
        else if (kind == "DEPOSIT")
        {
            await ReceiptLines.MoveDepositAsync(context, depositId!.Value, "DEPOSITED", cancellationToken).ConfigureAwait(false);
        }

        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "MATCHED", "UNMATCHED", commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion = version, receiptId, depositId, kind });
    }
}
