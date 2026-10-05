using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Finance.ExchangeRates;

/// <summary>
/// E-USD-2, E-USD1-02-2/4: Tesorería or the Contador prepares the rate (DOP per USD, 4 decimals) of today or a past day with its source;
/// a weekday holiday is entered with the last business day's rate (E-USD1-02-7).
/// </summary>
public sealed record PrepareExchangeRate(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Currency, DateOnly RateDate, decimal Rate, string Source) : ICommand;

/// <summary>E-USD1-01-1, E-USD1-02-3: the Controller (not who prepared it) puts it in force; it supersedes the rate in force that day.</summary>
public sealed record ApproveExchangeRate(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RateId, long ExpectedVersion) : ICommand;

/// <summary>A DRAFT rate entered by mistake is discarded.</summary>
public sealed record DiscardExchangeRate(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RateId, long ExpectedVersion) : ICommand;

public static class ExchangeRateErrors
{
    public const string Invalid = "EXCHANGE_RATE_INVALID";
    public const string Future = "EXCHANGE_RATE_FUTURE";
    public const string Missing = "EXCHANGE_RATE_MISSING";
    public const string NotFound = "EXCHANGE_RATE_NOT_FOUND";
    public const string InvalidState = "EXCHANGE_RATE_INVALID_STATE";
    public const string VersionConflict = "EXCHANGE_RATE_VERSION_CONFLICT";
}

/// <summary>The rate a document dated <see cref="ForDate"/> uses, and the day it was published.</summary>
public sealed record ApplicableRate(string Currency, DateOnly ForDate, DateOnly RateDate, decimal Rate);

public static class ExchangeRateBook
{
    public const string Aggregate = "ExchangeRate";

    /// <summary>
    /// E-USD-2, E-USD1-02-1/7: the ACTIVE rate of the document's day; on a Saturday or Sunday, the last ACTIVE rate before it. A weekday
    /// without its rate refuses the document (a holiday is entered by Tesorería with the previous business day's rate).
    /// </summary>
    public static async Task<ApplicableRate> ForDateAsync(DbConnection connection, DbTransaction? transaction, Guid companyId, string currency, DateOnly date, CancellationToken cancellationToken)
    {
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var found = (await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT rate_date, rate FROM fin.exchange_rate
            WHERE company_id = @c AND currency = @cur AND status = 'ACTIVE' AND (rate_date = @d OR (@weekend AND rate_date < @d))
            ORDER BY rate_date DESC LIMIT 1
            """,
            r => new ApplicableRate(currency, date, r.Date(0), r.GetDecimal(1)),
            cancellationToken,
            ("c", companyId),
            ("cur", currency),
            ("d", date),
            ("weekend", weekend)).ConfigureAwait(false)).SingleOrDefault();
        return found ?? throw new DomainException(
            ExchangeRateErrors.Missing,
            $"There is no approved {currency} rate for {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: Tesorería enters it and the Controller approves it (E-USD-2).");
    }

    /// <summary>A peso amount from a USD one at a rate, to 2 decimals (E-USD1-01-2).</summary>
    public static decimal ToPesos(decimal amount, decimal rate) => decimal.Round(amount * rate, 2, MidpointRounding.AwayFromZero);

    internal sealed record Row(Guid Id, string Currency, DateOnly RateDate, decimal Rate, string Status, Guid PreparedBy, long Version);

    internal static async Task<Row> LockAsync(CommandContext context, Guid rateId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT rate_id, currency, rate_date, rate, status, prepared_by, version FROM fin.exchange_rate WHERE company_id = @c AND rate_id = @r FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetDecimal(3), r.GetString(4), r.GetGuid(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", rateId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(ExchangeRateErrors.NotFound, "The rate does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(ExchangeRateErrors.VersionConflict, $"The rate is at version {row.Version}, not {expectedVersion}.");
    }

    internal static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal static async Task<long> NextEventVersionAsync(CommandContext context, Guid rateId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection, context.Transaction,
            "SELECT coalesce(max(aggregate_version), 0) FROM core.domain_event WHERE company_id = @c AND aggregate_type = 'ExchangeRate' AND aggregate_id = @r",
            ("c", context.CompanyId), ("r", rateId));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! + 1;
    }

    internal static string Text(decimal rate) => rate.ToString("0.0000", CultureInfo.InvariantCulture);
}

[RequiresPermission("exchange_rate:prepare")]
public sealed class PrepareExchangeRateHandler : ICommandHandler<PrepareExchangeRate>
{
    public string CommandType => "Finance.PrepareExchangeRate";

    public async Task<string> HandleAsync(PrepareExchangeRate command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var currency = (command.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency != "USD")
        {
            throw new DomainException(ExchangeRateErrors.Invalid, "The only foreign currency is USD (E-USD-1).");
        }

        if (command.Rate <= 0m || decimal.Round(command.Rate, 4) != command.Rate)
        {
            throw new DomainException(ExchangeRateErrors.Invalid, "The rate is positive, with at most 4 decimals.");
        }

        if (command.RateDate > BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))
        {
            throw new DomainException(ExchangeRateErrors.Future, "A rate is entered for today or a past day (E-USD1-02-2).");
        }

        var source = (command.Source ?? string.Empty).Trim();
        if (source.Length is 0 or > 200)
        {
            throw new DomainException(ExchangeRateErrors.Invalid, "Say where the rate comes from (1 to 200 characters).");
        }

        var preparer = await ExchangeRateBook.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("ExchangeRatePrepared", 1, ExchangeRateBook.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { rateId = context.ResultRef, currency, rateDate = command.RateDate, rate = ExchangeRateBook.Text(command.Rate), source }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO fin.exchange_rate (rate_id, company_id, currency, rate_date, rate, source, status, prepared_by, version) VALUES (@id, @c, @cur, @d, @r, @s, 'DRAFT', @by, 1)",
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("cur", currency),
            ("d", command.RateDate),
            ("r", command.Rate),
            ("s", source),
            ("by", preparer)).ConfigureAwait(false);
        await context.AppendStateAsync(ExchangeRateBook.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { rateId = context.ResultRef, currency, rateDate = command.RateDate, rate = ExchangeRateBook.Text(command.Rate), status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("exchange_rate:approve")]
public sealed class ApproveExchangeRateHandler : ICommandHandler<ApproveExchangeRate>
{
    public string CommandType => "Finance.ApproveExchangeRate";

    public async Task<string> HandleAsync(ApproveExchangeRate command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('exchange-rate:' || @c, 0))", cancellationToken,
            ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var row = await ExchangeRateBook.LockAsync(context, command.RateId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(ExchangeRateErrors.InvalidState, $"The rate is {row.Status}.");
        }

        var approver = await ExchangeRateBook.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FinanceErrors.FourEyes, "A rate is approved by someone other than who prepared it.");
        }

        var previous = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT rate_id, version FROM fin.exchange_rate WHERE company_id = @c AND currency = @cur AND rate_date = @d AND status = 'ACTIVE' FOR UPDATE",
            r => (Id: r.GetGuid(0), Version: r.GetInt64(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("cur", row.Currency),
            ("d", row.RateDate)).ConfigureAwait(false)).SingleOrDefault();
        var eventId = await context.AppendEventAsync(
            new EventDraft("ExchangeRateApproved", 1, ExchangeRateBook.Aggregate, row.Id, await ExchangeRateBook.NextEventVersionAsync(context, row.Id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { rateId = row.Id, currency = row.Currency, rateDate = row.RateDate, rate = ExchangeRateBook.Text(row.Rate), supersedes = previous == default ? (Guid?)null : previous.Id }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (previous != default)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.exchange_rate SET status = 'SUPERSEDED', version = @v WHERE rate_id = @r", cancellationToken,
                ("v", previous.Version + 1), ("r", previous.Id)).ConfigureAwait(false);
            await context.AppendStateAsync(ExchangeRateBook.Aggregate, previous.Id, "DOCUMENT", "ACTIVE", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.exchange_rate SET status = 'ACTIVE', approved_by = @by, approved_at = now(), version = @v WHERE rate_id = @r", cancellationToken,
            ("by", approver), ("v", row.Version + 1), ("r", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(ExchangeRateBook.Aggregate, row.Id, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            rateId = row.Id,
            currency = row.Currency,
            rateDate = row.RateDate,
            rate = ExchangeRateBook.Text(row.Rate),
            status = "ACTIVE",
            superseded = previous == default ? (Guid?)null : previous.Id,
            version = row.Version + 1,
        });
    }
}

[RequiresPermission("exchange_rate:prepare")]
public sealed class DiscardExchangeRateHandler : ICommandHandler<DiscardExchangeRate>
{
    public string CommandType => "Finance.DiscardExchangeRate";

    public async Task<string> HandleAsync(DiscardExchangeRate command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ExchangeRateBook.LockAsync(context, command.RateId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(ExchangeRateErrors.InvalidState, $"Only a DRAFT rate is discarded; this one is {row.Status}.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("ExchangeRateDiscarded", 1, ExchangeRateBook.Aggregate, row.Id, await ExchangeRateBook.NextEventVersionAsync(context, row.Id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { rateId = row.Id }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.exchange_rate SET status = 'DISCARDED', version = @v WHERE rate_id = @r", cancellationToken,
            ("v", row.Version + 1), ("r", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(ExchangeRateBook.Aggregate, row.Id, "DOCUMENT", "DRAFT", "DISCARDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { rateId = row.Id, status = "DISCARDED", version = row.Version + 1 });
    }
}

/// <summary>E-USD1-02-5: the rates of a range of days (newest first), every status.</summary>
public sealed record ListExchangeRates(Guid CompanyId, Guid SessionId, DateOnly? From = null, DateOnly? To = null) : IQuery;

public sealed record ExchangeRateView(
    Guid RateId, string Currency, DateOnly RateDate, decimal Rate, string Source, string Status, string? PreparedBy, Guid PreparedById, string? ApprovedBy, DateTime? ApprovedAt, long Version);

public sealed record ExchangeRateList(IReadOnlyList<ExchangeRateView> Items);

[RequiresPermission("exchange_rate:read")]
public sealed class ListExchangeRatesHandler : IQueryHandler<ListExchangeRates>
{
    public string QueryType => "Finance.ListExchangeRates";

    public async Task<string> HandleAsync(ListExchangeRates query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.rate_id, x.currency, x.rate_date, x.rate, x.source, x.status, coalesce(p.display_name, p.email), x.prepared_by, coalesce(a.display_name, a.email), x.approved_at, x.version
            FROM fin.exchange_rate x JOIN iam.user p ON p.user_id = x.prepared_by LEFT JOIN iam.user a ON a.user_id = x.approved_by
            WHERE x.company_id = @c AND x.rate_date BETWEEN @from AND @to
            ORDER BY x.rate_date DESC, x.status <> 'DRAFT', x.version DESC
            """,
            r => new ExchangeRateView(
                r.GetGuid(0), r.GetString(1), r.Date(2), r.GetDecimal(3), r.GetString(4), r.GetString(5), r.NullableString(6), r.GetGuid(7), r.NullableString(8), r.NullableUtc(9), r.GetInt64(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("from", query.From ?? today.AddDays(-30)),
            ("to", query.To ?? today)).ConfigureAwait(false);
        return ApiJson.Serialize(new ExchangeRateList(items));
    }
}

/// <summary>E-USD1-02-5: the rate a document of <paramref name="Date"/> would use and the day it comes from; EXCHANGE_RATE_MISSING when none applies.</summary>
public sealed record GetExchangeRateForDate(Guid CompanyId, Guid SessionId, DateOnly Date, string Currency = "USD") : IQuery;

[RequiresPermission("exchange_rate:read")]
public sealed class GetExchangeRateForDateHandler : IQueryHandler<GetExchangeRateForDate>
{
    public string QueryType => "Finance.GetExchangeRateForDate";

    public async Task<string> HandleAsync(GetExchangeRateForDate query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        return ApiJson.Serialize(await ExchangeRateBook.ForDateAsync(context.Connection, context.Transaction, context.CompanyId, query.Currency, query.Date, cancellationToken).ConfigureAwait(false));
    }
}
