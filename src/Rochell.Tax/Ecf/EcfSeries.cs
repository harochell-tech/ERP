using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Tax.Ecf;

// VS4-02 (E-VS4-1, E-VS4-01-1/4): the DGII-authorized e-NCF ranges Core numbers from. The Especialista fiscal prepares a range (at the
// cut-over, from the first number the previous provider did not use); the Controller approves it, which closes the type's ACTIVE range.

/// <summary>E-VS4-01-4: a DRAFT range of an e-CF type (31, 32, 34, 44), numbers <paramref name="From"/>…<paramref name="To"/> (the 10 digits after E + type).</summary>
public sealed record PrepareEcfSeries(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string EcfType, long From, long To, DateOnly ValidUntil, string? DgiiAuthorization = null) : ICommand;

/// <summary>E-VS4-01-4: the Controller (not the preparer, step-up) activates a DRAFT range; the type's ACTIVE range, if any, is closed.</summary>
public sealed record ApproveEcfSeries(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SeriesId, long ExpectedVersion) : ICommand;

/// <summary>Discards a DRAFT range.</summary>
public sealed record DiscardEcfSeries(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SeriesId, long ExpectedVersion) : ICommand;

/// <summary>E-VS4-12: the Controller closes an ACTIVE range (its unused numbers are voided through Alanube, VS4-04).</summary>
public sealed record CloseEcfSeries(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SeriesId, long ExpectedVersion) : ICommand;

internal static class EcfSeriesBook
{
    public const string Aggregate = "EcfSeries";
    private const long MaxNumber = 9999999999; // type-limit: the 10 digits of an e-NCF

    public static readonly string[] Types = ["31", "32", "34", "44"];

    public sealed record Row(Guid Id, string EcfType, long From, long To, long Next, DateOnly ValidUntil, string Status, Guid PreparedBy, long Version);

    public static void Validate(string type, long from, long to)
    {
        if (!Types.Contains(type))
        {
            throw new DomainException(EcfErrors.SeriesInvalid, "The e-CF type is 31, 32, 34 or 44 (E-VS4-2).");
        }

        if (from < 1 || to < from || to > MaxNumber)
        {
            throw new DomainException(EcfErrors.SeriesInvalid, "The range runs from 1 to 9999999999, its end not before its start.");
        }
    }

    public static async Task<Row> LockAsync(CommandContext context, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT series_id, ecf_type, range_from, range_to, next_number, valid_until, status, prepared_by, version FROM tax.ecf_series WHERE company_id = @c AND series_id = @s FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.Date(5), r.GetString(6), r.GetGuid(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", id)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(EcfErrors.SeriesNotFound, "The e-NCF range does not exist.");
        return expectedVersion is null || expectedVersion == row.Version
            ? row
            : throw new DomainException(EcfErrors.VersionConflict, $"The range changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    public static async Task<long> TransitionAsync(
        CommandContext context, Row row, string to, string eventType, Guid? approvedBy, string commandType, CancellationToken cancellationToken, Guid? causation = null)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.Id, version, JsonSerializer.Serialize(new { seriesId = row.Id, ecfType = row.EcfType, status = to }), Publish: true,
                CausationId: causation),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE tax.ecf_series SET status = @s, approved_by = coalesce(approved_by, @by), approved_at = CASE WHEN CAST(@by AS uuid) IS NULL THEN approved_at ELSE now() END, version = @v WHERE series_id = @id",
            cancellationToken,
            ("s", to),
            ("by", (object?)approvedBy ?? DBNull.Value),
            ("v", version),
            ("id", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return version;
    }

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static string Encf(string type, long number) => "E" + type + number.ToString("D10", CultureInfo.InvariantCulture);
}

[RequiresPermission("ecf_series:prepare")]
public sealed class PrepareEcfSeriesHandler : ICommandHandler<PrepareEcfSeries>
{
    public string CommandType => "Tax.PrepareEcfSeries";

    public async Task<string> HandleAsync(PrepareEcfSeries command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        EcfSeriesBook.Validate(command.EcfType, command.From, command.To);
        var authorization = string.IsNullOrWhiteSpace(command.DgiiAuthorization) ? null : command.DgiiAuthorization.Trim();
        if (authorization is { Length: > 60 })
        {
            throw new DomainException(EcfErrors.SeriesInvalid, "The DGII authorization has at most 60 characters.");
        }

        var preparer = await EcfSeriesBook.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "EcfSeriesPrepared", 1, EcfSeriesBook.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new
                {
                    seriesId = context.ResultRef,
                    ecfType = command.EcfType,
                    from = EcfSeriesBook.Encf(command.EcfType, command.From),
                    to = EcfSeriesBook.Encf(command.EcfType, command.To),
                    validUntil = command.ValidUntil,
                    dgiiAuthorization = authorization,
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO tax.ecf_series (series_id, company_id, ecf_type, dgii_authorization, range_from, range_to, next_number, valid_until, status, prepared_by, version)
                VALUES (@id, @c, @t, @a, @f, @to, @f, @u, 'DRAFT', @by, 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("t", command.EcfType),
                ("a", (object?)authorization ?? DBNull.Value),
                ("f", command.From),
                ("to", command.To),
                ("u", command.ValidUntil),
                ("by", preparer)).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException ex) when (ex.SqlState == SqlStates.RaiseException)
        {
            throw new DomainException(EcfErrors.SeriesInvalid, "The range overlaps another range of the same type.");
        }

        await context.AppendStateAsync(EcfSeriesBook.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { seriesId = context.ResultRef, status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("ecf_series:approve", StepUp = true)]
public sealed class ApproveEcfSeriesHandler : ICommandHandler<ApproveEcfSeries>
{
    public string CommandType => "Tax.ApproveEcfSeries";

    public async Task<string> HandleAsync(ApproveEcfSeries command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await EcfSeriesBook.LockAsync(context, command.SeriesId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(EcfErrors.InvalidState, $"The range is {row.Status}: only a draft is approved.");
        }

        var approver = await EcfSeriesBook.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(EcfErrors.ApproverIsPreparer, "Who prepared a range does not approve it (E-VS4-01-4).");
        }

        var activeId = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT series_id FROM tax.ecf_series WHERE company_id = @c AND ecf_type = @t AND status = 'ACTIVE'", r => r.GetGuid(0),
            cancellationToken, ("c", context.CompanyId), ("t", row.EcfType)).ConfigureAwait(false)).SingleOrDefault();
        Guid? closed = null;
        if (activeId != Guid.Empty)
        {
            var active = await EcfSeriesBook.LockAsync(context, activeId, null, cancellationToken).ConfigureAwait(false);
            await EcfSeriesBook.TransitionAsync(context, active, "CLOSED", "EcfSeriesClosed", null, CommandType, cancellationToken).ConfigureAwait(false);
            closed = activeId;
        }

        var version = await EcfSeriesBook.TransitionAsync(context, row, "ACTIVE", "EcfSeriesApproved", approver, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { seriesId = row.Id, status = "ACTIVE", closedSeriesId = closed, version });
    }
}

[RequiresPermission("ecf_series:prepare")]
public sealed class DiscardEcfSeriesHandler : ICommandHandler<DiscardEcfSeries>
{
    public string CommandType => "Tax.DiscardEcfSeries";

    public async Task<string> HandleAsync(DiscardEcfSeries command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await EcfSeriesBook.LockAsync(context, command.SeriesId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(EcfErrors.InvalidState, $"The range is {row.Status}: only a draft is discarded.");
        }

        var version = await EcfSeriesBook.TransitionAsync(context, row, "DISCARDED", "EcfSeriesDiscarded", null, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { seriesId = row.Id, status = "DISCARDED", version });
    }
}

[RequiresPermission("ecf_series:approve", StepUp = true)]
public sealed class CloseEcfSeriesHandler : ICommandHandler<CloseEcfSeries>
{
    public string CommandType => "Tax.CloseEcfSeries";

    public async Task<string> HandleAsync(CloseEcfSeries command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await EcfSeriesBook.LockAsync(context, command.SeriesId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "ACTIVE")
        {
            throw new DomainException(EcfErrors.InvalidState, $"The range is {row.Status}: only an ACTIVE range is closed.");
        }

        var version = await EcfSeriesBook.TransitionAsync(context, row, "CLOSED", "EcfSeriesClosed", null, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { seriesId = row.Id, status = "CLOSED", unused = row.To - row.Next + 1, version });
    }
}

/// <summary>E-VS4-01-4: the ranges, newest first, with how many numbers are left.</summary>
public sealed record ListEcfSeries(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record EcfSeriesView(
    Guid SeriesId, string EcfType, string From, string To, string Next, long Remaining, DateOnly ValidUntil, string? DgiiAuthorization, string Status, string? PreparedBy,
    string? ApprovedBy, long Version);

public sealed record EcfSeriesList(IReadOnlyList<EcfSeriesView> Items);

[RequiresPermission("fiscal_report:read")]
public sealed class ListEcfSeriesHandler : IQueryHandler<ListEcfSeries>
{
    public string QueryType => "Tax.ListEcfSeries";

    public async Task<string> HandleAsync(ListEcfSeries query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.series_id, s.ecf_type, s.range_from, s.range_to, s.next_number, s.valid_until, s.dgii_authorization, s.status,
                   coalesce(p.display_name, p.email), coalesce(a.display_name, a.email), s.version
            FROM tax.ecf_series s
            LEFT JOIN iam.user p ON p.user_id = s.prepared_by
            LEFT JOIN iam.user a ON a.user_id = s.approved_by
            WHERE s.company_id = @c
            ORDER BY s.ecf_type, s.range_from DESC
            """,
            r => new EcfSeriesView(
                r.GetGuid(0), r.GetString(1), EcfSeriesBook.Encf(r.GetString(1), r.GetInt64(2)), EcfSeriesBook.Encf(r.GetString(1), r.GetInt64(3)),
                EcfSeriesBook.Encf(r.GetString(1), Math.Min(r.GetInt64(4), r.GetInt64(3))), r.GetInt64(3) - r.GetInt64(4) + 1, r.Date(5), r.NullableString(6), r.GetString(7),
                r.NullableString(8), r.NullableString(9), r.GetInt64(10)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new EcfSeriesList(items));
    }
}
