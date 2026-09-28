using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Treasury.Payments;

namespace Rochell.Treasury.Statements;

internal static class BankLines
{
    public const string Aggregate = "BankStatementLine";

    public sealed record Line(Guid StatementId, Guid BankAccountId, DateOnly ValueDate, string Direction, decimal Amount, string? Reference, string Description, string Status, Guid? MatchedPaymentId, long Version);

    public sealed record Payment(Guid BankAccountId, decimal Amount, DateOnly ValueDate, string Status, long Version, string PaymentNo);

    private const string LineColumns = "statement_id, bank_account_id, value_date, direction, amount, bank_reference, description, status, matched_payment_id, version";

    private static Line ReadLine(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetGuid(1), r.Date(2), r.GetString(3), r.GetDecimal(4), r.NullableString(5), r.GetString(6), r.GetString(7), r.NullableGuid(8), r.GetInt64(9));

    public static async Task<Line> LockLineAsync(CommandContext context, Guid lineId, long expectedVersion, CancellationToken cancellationToken)
    {
        var line = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            $"SELECT {LineColumns} FROM fin.bank_statement_line WHERE line_id = @id AND company_id = @c FOR UPDATE",
            ReadLine,
            cancellationToken,
            ("id", lineId),
            ("c", context.CompanyId)).ConfigureAwait(false)
            ?? throw new DomainException(StatementErrors.NotFound, "The statement line does not exist.");
        return line.Version == expectedVersion
            ? line
            : throw new DomainException(StatementErrors.VersionConflict, $"The statement line is at version {line.Version}, not {expectedVersion}.");
    }

    public static async Task<Guid?> MatchedPaymentAsync(CommandContext context, Guid lineId, CancellationToken cancellationToken)
        => await PaymentRules.ScalarAsync<Guid?>(
            context,
            "SELECT matched_payment_id FROM fin.bank_statement_line WHERE line_id = @id AND company_id = @c",
            cancellationToken,
            ("id", lineId),
            ("c", context.CompanyId)).ConfigureAwait(false);

    public static async Task<Payment> LockPaymentAsync(CommandContext context, Guid paymentId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               "SELECT bank_account_id, amount, value_date, status::text, version, payment_no FROM fin.payment WHERE payment_id = @id AND company_id = @c FOR UPDATE",
               r => new Payment(r.GetGuid(0), r.GetDecimal(1), r.Date(2), r.GetString(3), r.GetInt64(4), r.GetString(5)),
               cancellationToken,
               ("id", paymentId),
               ("c", context.CompanyId)).ConfigureAwait(false)
           ?? throw new DomainException(PaymentErrors.NotFound, "The payment does not exist.");

    /// <summary>Third in the lock order (E-VS2-05-8); shared, so matches never wait on each other but a close of the account does.</summary>
    public static async Task ShareBankAccountAsync(CommandContext context, Guid bankAccountId, CancellationToken cancellationToken)
        => await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT 1 FROM fin.bank_account WHERE bank_account_id = @b FOR SHARE",
            cancellationToken,
            ("b", bankAccountId)).ConfigureAwait(false);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

[RequiresPermission("bank_line:match")]
public sealed class MatchBankLineHandler : ICommandHandler<MatchBankLine>
{
    /// <summary>E-VS2-05-6: the bank debits the transfer on its value date or within the next 10 days.</summary>
    public const int MatchWindowDays = 10;

    public string CommandType => "Treasury.MatchBankLine";

    public async Task<string> HandleAsync(MatchBankLine command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // Lock order (E-VS2-05-8): payment → statement line → bank account.
        var payment = await BankLines.LockPaymentAsync(context, command.PaymentId, cancellationToken).ConfigureAwait(false);
        if (payment.Version != command.ExpectedPaymentVersion)
        {
            throw new DomainException(StatementErrors.VersionConflict, $"The payment is at version {payment.Version}, not {command.ExpectedPaymentVersion}.");
        }

        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedLineVersion, cancellationToken).ConfigureAwait(false);
        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);

        if (line.Status != "UNMATCHED")
        {
            throw new DomainException(StatementErrors.LineNotUnmatched, $"The line is {line.Status}.");
        }

        if (line.BankAccountId != payment.BankAccountId)
        {
            throw new DomainException(StatementErrors.WrongBankAccount, "The line and the payment belong to different bank accounts.");
        }

        if (line.Amount != payment.Amount)
        {
            throw new DomainException(
                StatementErrors.AmountDiffers,
                $"The line is {PaymentRules.Money(line.Amount)} and the payment {PaymentRules.Money(payment.Amount)}; the amounts must be equal (E-VS2-05-6).");
        }

        if (line.Direction == StatementFile.Credit)
        {
            return await MatchReturnAsync(command, context, line, payment, cancellationToken).ConfigureAwait(false);
        }

        if (payment.Status != "RELEASED")
        {
            throw new DomainException(StatementErrors.PaymentNotReleased, $"The payment is {payment.Status}; only a RELEASED payment is matched to a DEBIT line (E-VS2-05-6).");
        }

        if (line.ValueDate < payment.ValueDate || line.ValueDate > payment.ValueDate.AddDays(MatchWindowDays))
        {
            throw new DomainException(
                StatementErrors.DateOutsideWindow,
                $"The line is dated {BankLines.Date(line.ValueDate)}, outside {BankLines.Date(payment.ValueDate)}…{BankLines.Date(payment.ValueDate.AddDays(MatchWindowDays))} (E-VS2-05-6).");
        }

        var lineVersion = line.Version + 1;
        var paymentVersion = payment.Version + 1;
        var matched = await context.AppendEventAsync(
            new EventDraft(
                "BankLineMatched",
                1,
                BankLines.Aggregate,
                command.LineId,
                lineVersion,
                JsonSerializer.Serialize(new
                {
                    lineId = command.LineId,
                    statementId = line.StatementId,
                    paymentId = command.PaymentId,
                    paymentNo = payment.PaymentNo,
                    amount = PaymentRules.Money(line.Amount),
                    lineValueDate = line.ValueDate,
                    paymentValueDate = payment.ValueDate,
                }),
                Publish: true,
                BusinessDate: line.ValueDate),
            cancellationToken).ConfigureAwait(false);
        var cleared = await context.AppendEventAsync(
            new EventDraft(
                "PaymentCleared",
                1,
                PaymentRules.Aggregate,
                command.PaymentId,
                paymentVersion,
                JsonSerializer.Serialize(new { paymentId = command.PaymentId, paymentNo = payment.PaymentNo, lineId = command.LineId, clearedOn = line.ValueDate }),
                Publish: true,
                BusinessDate: line.ValueDate,
                CausationId: matched),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = 'MATCHED', matched_payment_id = @p, version = @v WHERE line_id = @id",
            cancellationToken,
            ("p", command.PaymentId),
            ("v", lineVersion),
            ("id", command.LineId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.payment SET status = 'CLEARED', version = @v WHERE payment_id = @id",
            cancellationToken,
            ("v", paymentVersion),
            ("id", command.PaymentId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "UNMATCHED", "MATCHED", CommandType, matched, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(PaymentRules.Aggregate, command.PaymentId, "DOCUMENT", "RELEASED", "CLEARED", CommandType, cleared, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion, paymentId = command.PaymentId, paymentStatus = "CLEARED", paymentVersion });
    }

    /// <summary>
    /// E-VS2-05-10: the bank's return of a reversed transfer — a CREDIT line of the same account and amount, dated on or after the
    /// reversal, matched to the REVERSED payment whose DEBIT line is matched. The payment keeps its status.
    /// </summary>
    private async Task<string> MatchReturnAsync(MatchBankLine command, CommandContext context, BankLines.Line line, BankLines.Payment payment, CancellationToken cancellationToken)
    {
        if (payment.Status != "REVERSED")
        {
            throw new DomainException(StatementErrors.PaymentNotReversed, $"The payment is {payment.Status}; a CREDIT line is matched only as the return of a reversed payment (E-VS2-05-10).");
        }

        var matchedDirections = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT direction FROM fin.bank_statement_line WHERE matched_payment_id = @p",
            r => r.GetString(0),
            cancellationToken,
            ("p", command.PaymentId)).ConfigureAwait(false);
        if (!matchedDirections.Contains(StatementFile.Debit))
        {
            throw new DomainException(StatementErrors.ReturnWithoutTransfer, "The reversed payment has no matched DEBIT line: the bank never debited it, so there is no return to match (E-VS2-05-10).");
        }

        if (matchedDirections.Contains(StatementFile.Credit))
        {
            throw new DomainException(StatementErrors.ReturnAlreadyMatched, "The payment's return is already matched to another line.");
        }

        var reversedOn = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT business_date FROM core.domain_event WHERE company_id = @c AND aggregate_id = @p AND event_type = 'PaymentReversed'",
            r => r.Date(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", command.PaymentId)).ConfigureAwait(false)).Single();
        if (line.ValueDate < reversedOn)
        {
            throw new DomainException(
                StatementErrors.DateOutsideWindow,
                $"The return is dated {BankLines.Date(line.ValueDate)}, before the reversal on {BankLines.Date(reversedOn)} (E-VS2-05-10).");
        }

        var lineVersion = line.Version + 1;
        var matched = await context.AppendEventAsync(
            new EventDraft(
                "BankLineMatched",
                1,
                BankLines.Aggregate,
                command.LineId,
                lineVersion,
                JsonSerializer.Serialize(new
                {
                    lineId = command.LineId,
                    statementId = line.StatementId,
                    paymentId = command.PaymentId,
                    paymentNo = payment.PaymentNo,
                    kind = "RETURN",
                    amount = PaymentRules.Money(line.Amount),
                    lineValueDate = line.ValueDate,
                    reversedOn,
                }),
                Publish: true,
                BusinessDate: line.ValueDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = 'MATCHED', matched_payment_id = @p, version = @v WHERE line_id = @id",
            cancellationToken,
            ("p", command.PaymentId),
            ("v", lineVersion),
            ("id", command.LineId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "UNMATCHED", "MATCHED", CommandType, matched, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion, paymentId = command.PaymentId, paymentStatus = payment.Status, paymentVersion = payment.Version });
    }
}

[RequiresPermission("bank_line:unmatch", StepUp = true)]
public sealed class UnmatchBankLineHandler : ICommandHandler<UnmatchBankLine>
{
    public string CommandType => "Treasury.UnmatchBankLine";

    public async Task<string> HandleAsync(UnmatchBankLine command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(StatementErrors.ReasonRequired, "Unmatching a line needs a reason (E-VS2-05-8).");
        }

        // E-VS3-07-10: a receipt's or deposit's line.
        var (receiptId, depositId) = await ReceiptLines.MatchedAsync(context, command.LineId, cancellationToken).ConfigureAwait(false);
        if (receiptId is not null || depositId is not null)
        {
            return await ReceiptUnmatching.UnmatchAsync(command, context, receiptId, depositId, reason, CommandType, cancellationToken).ConfigureAwait(false);
        }

        // Lock order (E-VS2-05-8): payment → statement line → bank account. The line names its payment; it is read again under lock.
        var paymentId = await BankLines.MatchedPaymentAsync(context, command.LineId, cancellationToken).ConfigureAwait(false);
        var payment = paymentId is { } p ? await BankLines.LockPaymentAsync(context, p, cancellationToken).ConfigureAwait(false) : null;
        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (line.Status != "MATCHED" || payment is null || line.MatchedPaymentId != paymentId)
        {
            throw new DomainException(StatementErrors.LineNotMatched, $"The line is {line.Status}; only a MATCHED line is unmatched.");
        }

        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        if (line.Direction == StatementFile.Credit)
        {
            // E-VS2-05-10: a return matched by mistake goes back to UNMATCHED; the reversed payment does not change.
            var version = line.Version + 1;
            var eventId = await context.AppendEventAsync(
                new EventDraft(
                    "BankLineUnmatched",
                    1,
                    BankLines.Aggregate,
                    command.LineId,
                    version,
                    JsonSerializer.Serialize(new { lineId = command.LineId, statementId = line.StatementId, paymentId, paymentNo = payment.PaymentNo, kind = "RETURN", reason }),
                    Publish: true),
                cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE fin.bank_statement_line SET status = 'UNMATCHED', matched_payment_id = NULL, version = @v WHERE line_id = @id",
                cancellationToken,
                ("v", version),
                ("id", command.LineId)).ConfigureAwait(false);
            await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "MATCHED", "UNMATCHED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion = version, paymentId, paymentStatus = payment.Status, paymentVersion = payment.Version });
        }

        if (payment.Status != "CLEARED")
        {
            throw new DomainException(
                StatementErrors.PaymentReversed,
                $"The payment is {payment.Status}: the line of a reversed payment stays matched to it (E-VS2-04-1).");
        }

        var lineVersion = line.Version + 1;
        var paymentVersion = payment.Version + 1;
        var unmatched = await context.AppendEventAsync(
            new EventDraft(
                "BankLineUnmatched",
                1,
                BankLines.Aggregate,
                command.LineId,
                lineVersion,
                JsonSerializer.Serialize(new { lineId = command.LineId, statementId = line.StatementId, paymentId, paymentNo = payment.PaymentNo, reason }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        var uncleared = await context.AppendEventAsync(
            new EventDraft(
                "PaymentUncleared",
                1,
                PaymentRules.Aggregate,
                paymentId!.Value,
                paymentVersion,
                JsonSerializer.Serialize(new { paymentId, paymentNo = payment.PaymentNo, lineId = command.LineId, reason }),
                Publish: true,
                CausationId: unmatched),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = 'UNMATCHED', matched_payment_id = NULL, version = @v WHERE line_id = @id",
            cancellationToken,
            ("v", lineVersion),
            ("id", command.LineId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.payment SET status = 'RELEASED', version = @v WHERE payment_id = @id",
            cancellationToken,
            ("v", paymentVersion),
            ("id", paymentId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "MATCHED", "UNMATCHED", CommandType, unmatched, cancellationToken, reason).ConfigureAwait(false);
        await context.AppendStateAsync(PaymentRules.Aggregate, paymentId.Value, "DOCUMENT", "CLEARED", "RELEASED", CommandType, uncleared, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lineId = command.LineId, lineVersion, paymentId, paymentStatus = "RELEASED", paymentVersion });
    }
}

[RequiresPermission("bank_charge:recognize")]
public sealed class RecognizeBankChargeHandler : ICommandHandler<RecognizeBankCharge>
{
    public const string RuleCode = "R-10";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Treasury.RecognizeBankCharge";

    public async Task<string> HandleAsync(RecognizeBankCharge command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // Lock order: statement line → bank account → period × BANK-REC (engine).
        var line = await BankLines.LockLineAsync(context, command.LineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        await BankLines.ShareBankAccountAsync(context, line.BankAccountId, cancellationToken).ConfigureAwait(false);
        if (line.Status != "UNMATCHED")
        {
            throw new DomainException(StatementErrors.LineNotUnmatched, $"The line is {line.Status}.");
        }

        if (line.Direction != StatementFile.Debit)
        {
            throw new DomainException(StatementErrors.LineNotDebit, "A bank charge is a DEBIT line; a CREDIT line stays unmatched as an in-transit item or is matched as a payment's return (E-VS2-05-7, E-VS2-05-10).");
        }

        var inputs = new Dictionary<string, string> { ["value_date"] = BankLines.Date(line.ValueDate), ["description"] = line.Description };
        var lines = new List<PostingLineInput>
        {
            new("R10-DR-CHG", "charge_amount", line.Amount, Inputs: inputs),
            new("R10-CR-BANK", "charge_amount", line.Amount, SubledgerRef: line.BankAccountId, Inputs: inputs),
        };
        var now = context.Clock.UtcNow;
        var plan = await _engine.PrepareAsync(context, new PostingRequest(RuleCode, line.ValueDate, now, lines), cancellationToken).ConfigureAwait(false);

        var version = line.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankChargeRecognized",
                1,
                BankLines.Aggregate,
                command.LineId,
                version,
                JsonSerializer.Serialize(new
                {
                    lineId = command.LineId,
                    statementId = line.StatementId,
                    bankAccountId = line.BankAccountId,
                    amount = PaymentRules.Money(line.Amount),
                    valueDate = line.ValueDate,
                    description = line.Description,
                    postingDate = plan.PostingDate,
                    lateEntry = plan.LateEntry,
                }),
                Publish: true,
                BusinessDate: line.ValueDate),
            cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_statement_line SET status = 'CHARGE_RECOGNIZED', charge_event_id = @e, version = @v WHERE line_id = @id",
            cancellationToken,
            ("e", eventId),
            ("v", version),
            ("id", command.LineId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankLines.Aggregate, command.LineId, "DOCUMENT", "UNMATCHED", "CHARGE_RECOGNIZED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            lineId = command.LineId,
            status = "CHARGE_RECOGNIZED",
            version,
            journalId = journal.JournalId,
            postingDate = journal.PostingDate,
            lateEntry = journal.LateEntry,
        });
    }
}
