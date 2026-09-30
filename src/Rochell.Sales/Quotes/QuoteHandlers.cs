using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Orders;

namespace Rochell.Sales.Quotes;

public static class QuoteErrors
{
    public const string NotFound = "QUOTE_NOT_FOUND";
    public const string InvalidState = "QUOTE_INVALID_STATE";
    public const string VersionConflict = "QUOTE_VERSION_CONFLICT";
    public const string ValidityInvalid = "QUOTE_VALIDITY_INVALID";
    public const string NothingToApprove = "QUOTE_NOTHING_TO_APPROVE";
    public const string PriceApprovalRequired = "QUOTE_PRICE_APPROVAL_REQUIRED";
    public const string SamePerson = "QUOTE_SAME_PERSON";
    public const string ReasonRequired = "QUOTE_REASON_REQUIRED";
    public const string Expired = "QUOTE_EXPIRED";
}

internal static class Quotes
{
    public const string Aggregate = "Quote";

    public sealed record Header(Guid PlantId, DateOnly ValidUntil, string Term, string? Site, string? CustomerRef, string? Notes);

    public sealed record PricedLine(int LineNo, Guid ItemId, string Uom, decimal Quantity, decimal ListPrice, decimal UnitPrice, decimal Net)
    {
        public bool Special => UnitPrice < ListPrice;
    }

    public sealed record Row(
        Guid QuoteId, string QuoteNo, Guid PartyId, string Status, DateOnly ValidUntil, int LinesVersion, int? ApprovedLinesVersion, Guid CreatedBy, long Version);

    public static string M(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>E-QUO1-02-4: the delivery term and site as the order's (E-VS3-5), the customer reference, the notes and a validity from today.</summary>
    public static Header ValidateHeader(CommandContext context, Guid plantId, DateOnly validUntil, string? term, string? site, string? customerRef, string? notes)
    {
        var order = Orders.Orders.ValidateHeader(term, site, null, customerRef, plantId);
        if (validUntil < SalesSql.Today(context))
        {
            throw new DomainException(QuoteErrors.ValidityInvalid, "A quote is valid until today or a later date.");
        }

        return new Header(plantId, validUntil, order.Term, order.Site, order.PoRef, SalesSql.Optional(notes, 1000, "The notes"));
    }

    /// <summary>E-QUO1-02-3: a DRAFT or ACTIVE customer; a blocked one or a party that is not a customer is refused.</summary>
    public static async Task EnsureCustomerAsync(CommandContext context, Guid partyId, CancellationToken cancellationToken)
    {
        var status = await Orders.Orders.CustomerStatusAsync(context, partyId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotCustomer, "The party is not a customer.");
        if (status is not ("DRAFT" or "ACTIVE"))
        {
            throw new DomainException(OrderErrors.CustomerNotActive, $"The customer is {status}.");
        }
    }

    /// <summary>
    /// E-QUO1-02-2: the list price in force for (item, unit) — missing refuses the line with PRICE_MISSING — and the quoted price, the
    /// list's when none is given. Nets are rounded to 2 decimals like the order's.
    /// </summary>
    public static Task<(Guid PriceListVersionId, List<PricedLine> Lines, decimal Total)> PriceAsync(
        CommandContext context, Guid plantId, IReadOnlyList<QuoteLineInput>? input, CancellationToken cancellationToken)
        => PriceAsync(context.Connection, context.Transaction, context.CompanyId, plantId, input, cancellationToken);

    /// <summary>The same pricing on any connection (E-UX4-3: the read-only preview of a quote runs it too).</summary>
    public static async Task<(Guid PriceListVersionId, List<PricedLine> Lines, decimal Total)> PriceAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, Guid companyId, Guid plantId, IReadOnlyList<QuoteLineInput>? input,
        CancellationToken cancellationToken)
    {
        var lines = input ?? [];
        if (lines.Count == 0)
        {
            throw new DomainException(SalesErrors.LinesRequired, "A quote has at least one line.");
        }

        if (await SalesSql.ScalarAsync<Guid?>(connection, transaction, "SELECT plant_id FROM md.plant WHERE company_id = @c AND plant_id = @p", cancellationToken, ("c", companyId), ("p", plantId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(SalesErrors.NotFound, "The plant does not exist.");
        }

        var list = await SalesSql.ScalarAsync<Guid?>(
            connection, transaction, "SELECT price_list_version_id FROM sal.price_list_version WHERE company_id = @c AND status = 'ACTIVE'", cancellationToken, ("c", companyId)).ConfigureAwait(false)
            ?? throw new DomainException(OrderErrors.PriceListMissing, "There is no approved price list.");
        var seen = new HashSet<(Guid, string)>();
        var priced = new List<PricedLine>();
        foreach (var line in lines)
        {
            var uom = (line.Uom ?? string.Empty).Trim();
            if (!seen.Add((line.ItemId, uom)))
            {
                throw new DomainException(SalesErrors.DuplicateLine, "Each item and unit appears once in a quote.");
            }

            var quantity = SalesSql.Positive(line.Quantity, 6, "The quantity");
            var listPrice = await SalesSql.ScalarAsync<decimal?>(
                connection, transaction, "SELECT unit_price FROM sal.price_list_line WHERE price_list_version_id = @l AND item_id = @i AND uom = @u", cancellationToken,
                ("l", list), ("i", line.ItemId), ("u", uom)).ConfigureAwait(false)
                ?? throw new DomainException(OrderErrors.PriceMissing, $"The price list in force has no price for item {line.ItemId} in {uom}.");
            var unitPrice = line.UnitPrice is { } given ? SalesSql.Positive(given, 4, "The unit price") : listPrice;
            var net = decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
            if (net <= 0m)
            {
                throw new DomainException(SalesErrors.AmountInvalid, "The quantity is too small to be priced.");
            }

            priced.Add(new PricedLine(priced.Count + 1, line.ItemId, uom, quantity, listPrice, unitPrice, net));
        }

        return (list, priced, priced.Sum(l => l.Net));
    }

    public static async Task WriteLinesAsync(CommandContext context, Guid quoteId, int linesVersion, IEnumerable<PricedLine> lines, CancellationToken cancellationToken)
    {
        foreach (var l in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO sal.quote_line (line_id, company_id, quote_id, lines_version, line_no, item_id, uom, quantity, list_price, unit_price, net_amount)
                VALUES (@id, @c, @q, @v, @no, @i, @u, @qty, @lp, @p, @n)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("q", quoteId),
                ("v", linesVersion),
                ("no", l.LineNo),
                ("i", l.ItemId),
                ("u", l.Uom),
                ("qty", l.Quantity),
                ("lp", l.ListPrice),
                ("p", l.UnitPrice),
                ("n", l.Net)).ConfigureAwait(false);
        }
    }

    public static object LinesPayload(IEnumerable<PricedLine> lines)
        => lines.Select(l => new { itemId = l.ItemId, uom = l.Uom, quantity = M(l.Quantity), listPrice = M(l.ListPrice), unitPrice = M(l.UnitPrice), net = M(l.Net) }).ToList();

    public static async Task<Row> LockAsync(CommandContext context, Guid quoteId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT quote_id, quote_no, party_id, status, valid_until, lines_version, approved_lines_version, created_by, version
            FROM sal.quote WHERE company_id = @c AND quote_id = @q FOR UPDATE
            """,
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.Date(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetGuid(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("q", quoteId)).ConfigureAwait(false)
            ?? throw new DomainException(QuoteErrors.NotFound, "The quote does not exist.");
        return expectedVersion is null || row.Version == expectedVersion
            ? row
            : throw new DomainException(QuoteErrors.VersionConflict, $"The quote changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    public static void RequireStatus(Row row, params string[] statuses)
    {
        if (!statuses.Contains(row.Status))
        {
            throw new DomainException(QuoteErrors.InvalidState, $"The quote is {row.Status}.");
        }
    }

    public static string Reason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= 500 ? trimmed : throw new DomainException(QuoteErrors.ReasonRequired, "A reason of 1 to 500 characters is required.");
    }

    public static Task<bool> HasSpecialPricesAsync(CommandContext context, Row row, CancellationToken cancellationToken)
        => SalesSql.ScalarAsync<bool>(
            context,
            "SELECT EXISTS (SELECT 1 FROM sal.quote_line WHERE quote_id = @q AND lines_version = @v AND unit_price < list_price)",
            cancellationToken,
            ("q", row.QuoteId),
            ("v", row.LinesVersion));

    /// <summary>Changes the status (+1 version) with its event and state history; <paramref name="set"/> adds columns to the update.</summary>
    public static async Task<string> TransitionAsync(
        CommandContext context, Row row, string to, string eventType, string commandType, CancellationToken cancellationToken, string? reason = null,
        string set = "", params (string Name, object? Value)[] values)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.QuoteId, version, JsonSerializer.Serialize(new { quoteId = row.QuoteId, quoteNo = row.QuoteNo, status = to, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"UPDATE sal.quote SET status = @s, version = @v{set} WHERE quote_id = @q",
            cancellationToken,
            [("s", to), ("v", version), ("q", row.QuoteId), .. values]).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, row.QuoteId, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { quoteId = row.QuoteId, quoteNo = row.QuoteNo, status = to, version });
    }

    /// <summary>Creates a DRAFT quote COT-… (numbered under a lock, as PV-…) with its lines and state history.</summary>
    public static async Task<string> InsertAsync(
        CommandContext context, Guid partyId, Header header, Guid priceList, List<PricedLine> lines, decimal total, Guid? copiedFrom, string commandType, CancellationToken cancellationToken)
    {
        var creator = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "quote-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(quote_no from 5)::int) FROM sal.quote WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var quoteNo = "COT-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var today = SalesSql.Today(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "QuoteCreated",
                1,
                Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    quoteId = context.ResultRef,
                    quoteNo,
                    partyId,
                    plantId = header.PlantId,
                    validUntil = header.ValidUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    deliveryTermCode = header.Term,
                    priceListVersionId = priceList,
                    copiedFromQuoteId = copiedFrom,
                    totalNet = M(total),
                    lines = LinesPayload(lines),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.quote (quote_id, company_id, quote_no, party_id, plant_id, quote_date, valid_until, delivery_term_code, site_address, customer_ref, notes,
              price_list_version_id, status, total_net, lines_version, created_by, copied_from_quote_id, version)
            VALUES (@id, @c, @no, @p, @plant, @date, @until, @term, @site, @ref, @notes, @list, 'DRAFT', @total, 1, @by, @from, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", quoteNo),
            ("p", partyId),
            ("plant", header.PlantId),
            ("date", today),
            ("until", header.ValidUntil),
            ("term", header.Term),
            ("site", header.Site),
            ("ref", header.CustomerRef),
            ("notes", header.Notes),
            ("list", priceList),
            ("total", total),
            ("by", creator),
            ("from", copiedFrom)).ConfigureAwait(false);
        await WriteLinesAsync(context, context.ResultRef, 1, lines, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { quoteId = context.ResultRef, quoteNo, status = "DRAFT", totalNet = M(total), special = lines.Any(l => l.Special), version = 1 });
    }
}

[RequiresPermission("quote:manage")]
public sealed class CreateQuoteHandler : ICommandHandler<CreateQuote>
{
    public string CommandType => "Sales.CreateQuote";

    public async Task<string> HandleAsync(CreateQuote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = Quotes.ValidateHeader(context, command.PlantId, command.ValidUntil, command.DeliveryTermCode, command.SiteAddress, command.CustomerRef, command.Notes);
        await Quotes.EnsureCustomerAsync(context, command.PartyId, cancellationToken).ConfigureAwait(false);
        var (list, lines, total) = await Quotes.PriceAsync(context, header.PlantId, command.Lines, cancellationToken).ConfigureAwait(false);
        return await Quotes.InsertAsync(context, command.PartyId, header, list, lines, total, null, CommandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:manage")]
public sealed class UpdateDraftQuoteHandler : ICommandHandler<UpdateDraftQuote>
{
    public string CommandType => "Sales.UpdateDraftQuote";

    public async Task<string> HandleAsync(UpdateDraftQuote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = Quotes.ValidateHeader(context, command.PlantId, command.ValidUntil, command.DeliveryTermCode, command.SiteAddress, command.CustomerRef, command.Notes);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "DRAFT");
        var (list, lines, total) = await Quotes.PriceAsync(context, header.PlantId, command.Lines, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var linesVersion = row.LinesVersion + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "QuoteDraftUpdated",
                1,
                Quotes.Aggregate,
                row.QuoteId,
                version,
                JsonSerializer.Serialize(new { quoteId = row.QuoteId, quoteNo = row.QuoteNo, linesVersion, priceListVersionId = list, totalNet = Quotes.M(total), lines = Quotes.LinesPayload(lines) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE sal.quote SET plant_id = @plant, valid_until = @until, delivery_term_code = @term, site_address = @site, customer_ref = @ref, notes = @notes,
              price_list_version_id = @list, total_net = @total, lines_version = @lv, version = @v
            WHERE quote_id = @q
            """,
            cancellationToken,
            ("plant", header.PlantId),
            ("until", header.ValidUntil),
            ("term", header.Term),
            ("site", header.Site),
            ("ref", header.CustomerRef),
            ("notes", header.Notes),
            ("list", list),
            ("total", total),
            ("lv", linesVersion),
            ("v", version),
            ("q", row.QuoteId)).ConfigureAwait(false);
        await Quotes.WriteLinesAsync(context, row.QuoteId, linesVersion, lines, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { quoteId = row.QuoteId, quoteNo = row.QuoteNo, status = "DRAFT", totalNet = Quotes.M(total), special = lines.Any(l => l.Special), version });
    }
}

[RequiresPermission("quote:manage")]
public sealed class SubmitQuoteForApprovalHandler : ICommandHandler<SubmitQuoteForApproval>
{
    public string CommandType => "Sales.SubmitQuoteForApproval";

    public async Task<string> HandleAsync(SubmitQuoteForApproval command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "DRAFT");
        if (!await Quotes.HasSpecialPricesAsync(context, row, cancellationToken).ConfigureAwait(false) || row.ApprovedLinesVersion == row.LinesVersion)
        {
            throw new DomainException(QuoteErrors.NothingToApprove, "No line is below its list price without approval; send the quote.");
        }

        return await Quotes.TransitionAsync(context, row, "PENDING_APPROVAL", "QuoteSubmitted", CommandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:approve_price", StepUp = true)]
public sealed class ApproveQuotePricesHandler : ICommandHandler<ApproveQuotePrices>
{
    public string CommandType => "Sales.ApproveQuotePrices";

    public async Task<string> HandleAsync(ApproveQuotePrices command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "PENDING_APPROVAL");
        var approver = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.CreatedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(QuoteErrors.SamePerson, "The author of a quote does not approve its prices (E-QUO1-11).");
        }

        return await Quotes.TransitionAsync(
            context, row, "DRAFT", "QuotePricesApproved", CommandType, cancellationToken, null,
            ", price_approved_by = @by, price_approved_at = @at, approved_lines_version = @lv", ("by", approver), ("at", context.Clock.UtcNow), ("lv", row.LinesVersion)).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:approve_price")]
public sealed class ReturnQuoteToDraftHandler : ICommandHandler<ReturnQuoteToDraft>
{
    public string CommandType => "Sales.ReturnQuoteToDraft";

    public async Task<string> HandleAsync(ReturnQuoteToDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Quotes.Reason(command.Reason);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "PENDING_APPROVAL");
        return await Quotes.TransitionAsync(context, row, "DRAFT", "QuoteReturned", CommandType, cancellationToken, reason).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:manage")]
public sealed class SendQuoteHandler : ICommandHandler<SendQuote>
{
    public string CommandType => "Sales.SendQuote";

    public async Task<string> HandleAsync(SendQuote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "DRAFT");
        if (row.ValidUntil < SalesSql.Today(context))
        {
            throw new DomainException(QuoteErrors.Expired, $"The quote was valid until {row.ValidUntil:yyyy-MM-dd}; copy it with a new validity.");
        }

        if (row.ApprovedLinesVersion != row.LinesVersion && await Quotes.HasSpecialPricesAsync(context, row, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(QuoteErrors.PriceApprovalRequired, "A price below the list needs the approval of the current lines (E-QUO1-3).");
        }

        return await Quotes.TransitionAsync(context, row, "SENT", "QuoteSent", CommandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:manage")]
public sealed class MarkQuoteLostHandler : ICommandHandler<MarkQuoteLost>
{
    public string CommandType => "Sales.MarkQuoteLost";

    public async Task<string> HandleAsync(MarkQuoteLost command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Quotes.Reason(command.Reason);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "SENT");
        return await Quotes.TransitionAsync(context, row, "LOST", "QuoteLost", CommandType, cancellationToken, reason, ", closing_reason = @r", ("r", reason)).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:manage")]
public sealed class CancelQuoteHandler : ICommandHandler<CancelQuote>
{
    public string CommandType => "Sales.CancelQuote";

    public async Task<string> HandleAsync(CancelQuote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Quotes.Reason(command.Reason);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "DRAFT", "SENT");
        return await Quotes.TransitionAsync(context, row, "CANCELLED", "QuoteCancelled", CommandType, cancellationToken, reason, ", closing_reason = @r", ("r", reason)).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:manage")]
public sealed class CopyQuoteHandler : ICommandHandler<CopyQuote>
{
    public string CommandType => "Sales.CopyQuote";

    private sealed record Source(Guid PartyId, Guid PlantId, string Term, string? Site, string? CustomerRef, string? Notes, int LinesVersion);

    public async Task<string> HandleAsync(CopyQuote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var source = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT party_id, plant_id, delivery_term_code, site_address, customer_ref, notes, lines_version FROM sal.quote WHERE company_id = @c AND quote_id = @q",
            r => new Source(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.NullableString(3), r.NullableString(4), r.NullableString(5), r.GetInt32(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("q", command.QuoteId)).ConfigureAwait(false)
            ?? throw new DomainException(QuoteErrors.NotFound, "The quote does not exist.");
        var header = Quotes.ValidateHeader(context, source.PlantId, command.ValidUntil, source.Term, source.Site, source.CustomerRef, source.Notes);
        await Quotes.EnsureCustomerAsync(context, source.PartyId, cancellationToken).ConfigureAwait(false);
        var input = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT item_id, uom, quantity, unit_price FROM sal.quote_line WHERE quote_id = @q AND lines_version = @v ORDER BY line_no",
            r => new QuoteLineInput(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3)),
            cancellationToken,
            ("q", command.QuoteId),
            ("v", source.LinesVersion)).ConfigureAwait(false);
        var (list, lines, total) = await Quotes.PriceAsync(context, header.PlantId, input, cancellationToken).ConfigureAwait(false);
        return await Quotes.InsertAsync(context, source.PartyId, header, list, lines, total, command.QuoteId, CommandType, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// E-QUO1-03-1…9: locks the quote, creates the DRAFT order PV-… with its plant, term, site, customer reference (the order's customer PO)
/// and the quoted quantities and prices, and marks the quote CONVERTED in the same transaction; the order's unique quote_id keeps it
/// to one order even when two conversions race (QUO-06). The order then follows its own flow (SubmitForCredit).
/// </summary>
[RequiresPermission("quote:manage")]
public sealed class ConvertQuoteHandler : ICommandHandler<ConvertQuote>
{
    public string CommandType => "Sales.ConvertQuote";

    private sealed record Source(Guid PlantId, string Term, string? Site, string? CustomerRef, Guid PriceList, decimal Total);

    public async Task<string> HandleAsync(ConvertQuote command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Quotes.LockAsync(context, command.QuoteId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        Quotes.RequireStatus(row, "SENT");
        if (row.ValidUntil < SalesSql.Today(context))
        {
            throw new DomainException(QuoteErrors.Expired, $"The quote was valid until {row.ValidUntil:yyyy-MM-dd}; copy it with a new validity.");
        }

        var customer = await Orders.Orders.CustomerStatusAsync(context, row.PartyId, cancellationToken).ConfigureAwait(false);
        if (customer != "ACTIVE")
        {
            throw new DomainException(OrderErrors.CustomerNotActive, $"The customer is {customer ?? "not a customer"}; activate it before converting (E-QUO1-1).");
        }

        var source = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, delivery_term_code, site_address, customer_ref, price_list_version_id, total_net FROM sal.quote WHERE quote_id = @q",
            r => new Source(r.GetGuid(0), r.GetString(1), r.NullableString(2), r.NullableString(3), r.GetGuid(4), r.GetDecimal(5)),
            cancellationToken,
            ("q", row.QuoteId)).ConfigureAwait(false))!;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT line_no, item_id, uom, quantity, unit_price, net_amount FROM sal.quote_line WHERE quote_id = @q AND lines_version = @v ORDER BY line_no",
            r => new Orders.Orders.PricedLine(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)),
            cancellationToken,
            ("q", row.QuoteId),
            ("v", row.LinesVersion)).ConfigureAwait(false);
        var header = new Orders.Orders.Header(source.PlantId, source.Term, source.Site, null, source.CustomerRef);
        var orderId = context.ResultRef;
        var orderNo = await Orders.Orders.InsertAsync(context, orderId, row.PartyId, header, source.PriceList, lines, source.Total, row.QuoteId, CommandType, cancellationToken)
            .ConfigureAwait(false);
        await Quotes.TransitionAsync(context, row, "CONVERTED", "QuoteConverted", CommandType, cancellationToken, null, ", sales_order_id = @o", ("o", orderId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = orderId, orderNo, quoteId = row.QuoteId, quoteNo = row.QuoteNo, status = "CONVERTED", totalNet = Quotes.M(source.Total) });
    }
}
