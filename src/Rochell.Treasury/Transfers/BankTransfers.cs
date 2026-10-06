using System.Globalization;
using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Treasury.Payments;

namespace Rochell.Treasury.Transfers;

/// <summary>
/// E-USD1-05b-1: Tesorería prepares a transfer between two of the company's accounts. <paramref name="Amount"/> is in USD when either
/// account is in USD (what is bought or sold), in pesos between two peso accounts. Between currencies <paramref name="ExchangeRate"/> is the
/// bank's rate and the peso side is USD × rate; between two USD accounts the pesos are at the day's approved rate; otherwise no rate.
/// </summary>
public sealed record PrepareBankTransfer(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid FromBankAccountId, Guid ToBankAccountId, DateOnly ValueDate, decimal Amount, decimal? ExchangeRate = null,
    string? BankReference = null) : ICommand;

/// <summary>PREPARED → VOIDED with a reason.</summary>
public sealed record VoidBankTransfer(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TransferId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-USD1-05b-1/2: someone other than the preparer releases it (step-up) on or after its value date; it is posted with P-42.</summary>
public sealed record ReleaseBankTransfer(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TransferId, long ExpectedVersion) : ICommand;

/// <summary>E-USD1-05b-1: the Controller reverses a RELEASED transfer exactly, today, with a reason.</summary>
public sealed record ReverseBankTransfer(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TransferId, long ExpectedVersion, string Reason) : ICommand;

public static class TransferErrors
{
    public const string Invalid = "TRANSFER_INVALID";
    public const string NotFound = "TRANSFER_NOT_FOUND";
    public const string InvalidState = "TRANSFER_INVALID_STATE";
    public const string VersionConflict = "TRANSFER_VERSION_CONFLICT";
    public const string SamePerson = "TRANSFER_SAME_PERSON";
}

internal static class BankTransfers
{
    public const string Aggregate = "BankTransfer";
    public const string P42 = "P-42";

    public sealed record Row(
        Guid Id, string Number, Guid From, Guid To, string FromCurrency, string ToCurrency, DateOnly ValueDate, decimal FromAmount, decimal ToAmount, decimal? Rate,
        decimal AmountDop, string Status, Guid PreparedBy, Guid? PostingEventId, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid transferId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT t.transfer_id, t.transfer_no, t.from_bank_account_id, t.to_bank_account_id, f.currency, d.currency, t.value_date, t.from_amount, t.to_amount, t.exchange_rate,
                   t.amount_dop, t.status, t.prepared_by, t.posting_event_id, t.version
            FROM fin.bank_transfer t JOIN fin.bank_account f ON f.bank_account_id = t.from_bank_account_id JOIN fin.bank_account d ON d.bank_account_id = t.to_bank_account_id
            WHERE t.company_id = @c AND t.transfer_id = @t FOR UPDATE OF t
            """,
            r => new Row(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4).Trim(), r.GetString(5).Trim(), r.Date(6), r.GetDecimal(7), r.GetDecimal(8),
                r.IsDBNull(9) ? null : r.GetDecimal(9), r.GetDecimal(10), r.GetString(11), r.GetGuid(12), r.IsDBNull(13) ? null : r.GetGuid(13), r.GetInt64(14)),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", transferId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(TransferErrors.NotFound, "The transfer does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(TransferErrors.VersionConflict, $"The transfer is at version {row.Version}, not {expectedVersion}.");
    }

    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string? RateText(decimal? rate) => rate?.ToString("0.0000", CultureInfo.InvariantCulture);
}

[RequiresPermission("payment:prepare")]
public sealed class PrepareBankTransferHandler : ICommandHandler<PrepareBankTransfer>
{
    public string CommandType => "Treasury.PrepareBankTransfer";

    public async Task<string> HandleAsync(PrepareBankTransfer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.FromBankAccountId == command.ToBankAccountId)
        {
            throw new DomainException(TransferErrors.Invalid, "A transfer goes between two different accounts.");
        }

        if (command.Amount <= 0m || decimal.Round(command.Amount, 2) != command.Amount)
        {
            throw new DomainException(TransferErrors.Invalid, "The amount is positive with at most 2 decimals.");
        }

        var reference = string.IsNullOrWhiteSpace(command.BankReference) ? null : command.BankReference.Trim();
        if (reference is { Length: > 80 })
        {
            throw new DomainException(TransferErrors.Invalid, "The bank reference has at most 80 characters.");
        }

        var currencies = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT bank_account_id, currency FROM fin.bank_account WHERE company_id = @c AND bank_account_id = ANY(@ids) AND status = 'ACTIVE'",
            r => (Id: r.GetGuid(0), Currency: r.GetString(1).Trim()),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", new[] { command.FromBankAccountId, command.ToBankAccountId })).ConfigureAwait(false)).ToDictionary(a => a.Id, a => a.Currency);
        if (currencies.Count != 2)
        {
            throw new DomainException(PaymentErrors.BankAccountNotActive, "Both accounts must be ACTIVE accounts of the company.");
        }

        var (from, to) = (currencies[command.FromBankAccountId], currencies[command.ToBankAccountId]);
        decimal? rate;
        if (from == to)
        {
            if (command.ExchangeRate is not null)
            {
                throw new DomainException(TransferErrors.Invalid, "A transfer between accounts of one currency takes no rate.");
            }

            rate = from == "USD"
                ? (await ExchangeRateBook.ForDateAsync(context.Connection, context.Transaction, context.CompanyId, "USD", command.ValueDate, cancellationToken).ConfigureAwait(false)).Rate
                : null;
        }
        else
        {
            rate = command.ExchangeRate is { } r && r > 0m && decimal.Round(r, 4) == r
                ? r
                : throw new DomainException(TransferErrors.Invalid, "Type the rate the bank applied to buy or sell the USD (positive, at most 4 decimals).");
        }

        // E-USD1-05b-2: the amount is in USD when a USD account takes part; the peso side is USD × rate.
        var pesos = from == "DOP" && to == "DOP" ? command.Amount : ExchangeRateBook.ToPesos(command.Amount, rate!.Value);
        var (fromAmount, toAmount) = (from, to) switch
        {
            ("DOP", "USD") => (pesos, command.Amount),
            ("USD", "DOP") => (command.Amount, pesos),
            _ => (command.Amount, command.Amount),
        };

        var preparer = await PaymentRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var year = command.ValueDate.Year;
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", cancellationToken,
            ("k", $"TRF-no:{context.CompanyId}:{year}")).ConfigureAwait(false);
        var stem = string.Create(CultureInfo.InvariantCulture, $"TRF-{year:D4}-");
        var last = await PaymentRules.ScalarAsync<int?>(
            context, "SELECT max(substring(transfer_no FROM 10)::int) FROM fin.bank_transfer WHERE company_id = @c AND transfer_no LIKE @stem || '%'", cancellationToken,
            ("c", context.CompanyId), ("stem", stem)).ConfigureAwait(false);
        var number = stem + ((last ?? 0) + 1).ToString("D6", CultureInfo.InvariantCulture);
        var id = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankTransferPrepared", 1, BankTransfers.Aggregate, id, 1,
                JsonSerializer.Serialize(new
                {
                    transferId = id,
                    transferNo = number,
                    fromBankAccountId = command.FromBankAccountId,
                    toBankAccountId = command.ToBankAccountId,
                    valueDate = command.ValueDate,
                    fromAmount = BankTransfers.Money(fromAmount),
                    toAmount = BankTransfers.Money(toAmount),
                    exchangeRate = BankTransfers.RateText(rate),
                    amountDop = BankTransfers.Money(pesos),
                    bankReference = reference,
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.bank_transfer (transfer_id, company_id, transfer_no, from_bank_account_id, to_bank_account_id, value_date, from_amount, to_amount, exchange_rate, amount_dop,
              bank_reference, status, prepared_by, version)
            VALUES (@id, @c, @no, @from, @to, @date, @fa, @ta, @rate, @dop, @ref, 'PREPARED', @by, 1)
            """,
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("no", number),
            ("from", command.FromBankAccountId),
            ("to", command.ToBankAccountId),
            ("date", command.ValueDate),
            ("fa", fromAmount),
            ("ta", toAmount),
            ("rate", rate),
            ("dop", pesos),
            ("ref", reference),
            ("by", preparer)).ConfigureAwait(false);
        await context.AppendStateAsync(BankTransfers.Aggregate, id, "DOCUMENT", null, "PREPARED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            transferId = id,
            transferNo = number,
            status = "PREPARED",
            fromAmount = BankTransfers.Money(fromAmount),
            toAmount = BankTransfers.Money(toAmount),
            exchangeRate = BankTransfers.RateText(rate),
            amountDop = BankTransfers.Money(pesos),
            version = 1,
        });
    }
}

[RequiresPermission("payment:void")]
public sealed class VoidBankTransferHandler : ICommandHandler<VoidBankTransfer>
{
    public string CommandType => "Treasury.VoidBankTransfer";

    public async Task<string> HandleAsync(VoidBankTransfer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? throw new DomainException(PaymentErrors.ReasonRequired, "Voiding a transfer needs a reason.")
            : command.Reason.Trim();
        var row = await BankTransfers.LockAsync(context, command.TransferId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PREPARED")
        {
            throw new DomainException(TransferErrors.InvalidState, $"The transfer is {row.Status}; only a PREPARED one is voided.");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("BankTransferVoided", 1, BankTransfers.Aggregate, row.Id, version, JsonSerializer.Serialize(new { transferId = row.Id, reason }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.bank_transfer SET status = 'VOIDED', version = @v WHERE transfer_id = @t", cancellationToken,
            ("v", version), ("t", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(BankTransfers.Aggregate, row.Id, "DOCUMENT", "PREPARED", "VOIDED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { transferId = row.Id, status = "VOIDED", version });
    }
}

[RequiresPermission("payment:release", StepUp = true)]
public sealed class ReleaseBankTransferHandler : ICommandHandler<ReleaseBankTransfer>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Treasury.ReleaseBankTransfer";

    public async Task<string> HandleAsync(ReleaseBankTransfer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await BankTransfers.LockAsync(context, command.TransferId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PREPARED")
        {
            throw new DomainException(TransferErrors.InvalidState, $"The transfer is {row.Status}; only a PREPARED one is released.");
        }

        var releaser = await PaymentRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (releaser == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(TransferErrors.SamePerson, "The preparer cannot release the transfer (E-USD1-05b-1).");
        }

        var now = context.Clock.UtcNow;
        if (row.ValueDate > BusinessCalendar.DefaultBusinessDate(now))
        {
            throw new DomainException(PaymentErrors.ValueDateInFuture, "A transfer is released on or after its value date.");
        }

        var active = await PaymentRules.ScalarAsync<long>(
            context, "SELECT count(*) FROM fin.bank_account WHERE bank_account_id = ANY(@ids) AND status = 'ACTIVE'", cancellationToken,
            ("ids", new[] { row.From, row.To })).ConfigureAwait(false);
        if (active != 2)
        {
            throw new DomainException(PaymentErrors.BankAccountNotActive, "Both accounts must still be ACTIVE.");
        }

        var rateText = row.Rate is { } rate ? $" (tasa {BankTransfers.RateText(rate)})" : string.Empty;
        var inputs = new Dictionary<string, string> { ["transfer_no"] = row.Number, ["rate_text"] = rateText };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                BankTransfers.P42,
                row.ValueDate,
                now,
                [
                    new PostingLineInput("P42-DR-BANK", "transfer_amount", row.AmountDop, SubledgerRef: row.To, AmountFc: row.ToCurrency == "USD" ? row.ToAmount : null, Inputs: inputs),
                    new PostingLineInput("P42-CR-BANK", "transfer_amount", row.AmountDop, SubledgerRef: row.From, AmountFc: row.FromCurrency == "USD" ? row.FromAmount : null, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankTransferReleased", 1, BankTransfers.Aggregate, row.Id, version,
                JsonSerializer.Serialize(new { transferId = row.Id, transferNo = row.Number, releasedBy = releaser, amountDop = BankTransfers.Money(row.AmountDop), postingDate = plan.PostingDate }),
                Publish: true, BusinessDate: row.ValueDate),
            cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.bank_transfer SET status = 'RELEASED', released_by = @by, posting_event_id = @e, version = @v WHERE transfer_id = @t",
            cancellationToken, ("by", releaser), ("e", eventId), ("v", version), ("t", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(BankTransfers.Aggregate, row.Id, "DOCUMENT", "PREPARED", "RELEASED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { transferId = row.Id, status = "RELEASED", journalId = journal.JournalId, version });
    }
}

[RequiresPermission("payment:reverse", StepUp = true)]
public sealed class ReverseBankTransferHandler : ICommandHandler<ReverseBankTransfer>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Treasury.ReverseBankTransfer";

    public async Task<string> HandleAsync(ReverseBankTransfer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length < 10)
        {
            throw new DomainException(PaymentErrors.ReasonRequired, "Reversing a transfer needs a reason of at least 10 characters.");
        }

        var row = await BankTransfers.LockAsync(context, command.TransferId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "RELEASED")
        {
            throw new DomainException(TransferErrors.InvalidState, $"The transfer is {row.Status}; only a RELEASED one is reversed.");
        }

        var journal = await PaymentRules.ScalarAsync<Guid>(
            context, "SELECT journal_id FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'AUTO'", cancellationToken,
            ("c", context.CompanyId), ("e", row.PostingEventId)).ConfigureAwait(false);
        var now = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(now);
        var plan = await _engine.PrepareReversalAsync(context, journal, businessDate, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankTransferReversed", 1, BankTransfers.Aggregate, row.Id, version, JsonSerializer.Serialize(new { transferId = row.Id, reversedJournalId = journal, reason }),
                Publish: true, BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.bank_transfer SET status = 'REVERSED', version = @v WHERE transfer_id = @t", cancellationToken,
            ("v", version), ("t", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(BankTransfers.Aggregate, row.Id, "DOCUMENT", "RELEASED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { transferId = row.Id, status = "REVERSED", journalId = reversal.JournalId, version });
    }
}

/// <summary>E-USD1-05b-1: the transfers, newest first.</summary>
public sealed record ListBankTransfers(Guid CompanyId, Guid SessionId, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record BankTransferView(
    Guid TransferId, string TransferNo, Guid FromBankAccountId, string FromCurrency, Guid ToBankAccountId, string ToCurrency, DateOnly ValueDate, decimal FromAmount, decimal ToAmount,
    decimal? ExchangeRate, decimal AmountDop, string? BankReference, string Status, string? PreparedBy, string? ReleasedBy, long Version);

public sealed record BankTransferList(IReadOnlyList<BankTransferView> Items, int Limit, int Offset);

[RequiresPermission("payment:read")]
public sealed class ListBankTransfersHandler : IQueryHandler<ListBankTransfers>
{
    public string QueryType => "Treasury.ListBankTransfers";

    public async Task<string> HandleAsync(ListBankTransfers query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT t.transfer_id, t.transfer_no, t.from_bank_account_id, f.currency, t.to_bank_account_id, d.currency, t.value_date, t.from_amount, t.to_amount, t.exchange_rate, t.amount_dop,
                   t.bank_reference, t.status, coalesce(pu.display_name, pu.email), coalesce(ru.display_name, ru.email), t.version
            FROM fin.bank_transfer t
            JOIN fin.bank_account f ON f.bank_account_id = t.from_bank_account_id
            JOIN fin.bank_account d ON d.bank_account_id = t.to_bank_account_id
            LEFT JOIN iam.user pu ON pu.user_id = t.prepared_by
            LEFT JOIN iam.user ru ON ru.user_id = t.released_by
            WHERE t.company_id = @c AND (CAST(@status AS text) IS NULL OR t.status = CAST(@status AS text))
            ORDER BY t.value_date DESC, t.transfer_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new BankTransferView(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3).Trim(), r.GetGuid(4), r.GetString(5).Trim(), r.Date(6), r.GetDecimal(7), r.GetDecimal(8), r.NullableDecimal(9),
                r.GetDecimal(10), r.NullableString(11), r.GetString(12), r.NullableString(13), r.NullableString(14), r.GetInt64(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new BankTransferList(items, query.Limit, query.Offset));
    }
}
