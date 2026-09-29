using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Tax.Authorizations;

/// <summary>An invoice line as the authorization sees it: product, unit, quantity and net.</summary>
public sealed record CoveredLine(Guid InvoiceLineId, Guid ItemId, string Uom, decimal Quantity, decimal Net);

/// <summary>What is returned to an authorization: the invoice line, and the quantity (0 for a price credit note) and net.</summary>
public sealed record ReleasedLine(Guid InvoiceLineId, decimal Quantity, decimal Net);

/// <summary>
/// E-FIS1-03: how Sales uses a fiscal authorization — check that an invoice is fully covered (D-05), consume it at issue and release
/// it on a void or a credit note. Callers run inside the command transaction; the authorization is locked first.
/// </summary>
public static class AuthorizationUsage
{
    private sealed record ScopeLine(int LineNo, Guid ItemId, string Uom, decimal QtyAvailable, decimal NetAvailable);

    /// <summary>
    /// Locks the authorization and checks it covers every line: same customer, ACTIVE, not past <c>valid_until</c>, each product and
    /// unit in the scope with enough quantity and net available. Returns the exemption for the Tax Engine.
    /// </summary>
    public static async Task<TaxExemption> CoverAsync(CommandContext context, Guid authorizationId, Guid partyId, IReadOnlyList<CoveredLine> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);
        var row = await AuthorizationStore.LockAsync(context, authorizationId, null, cancellationToken).ConfigureAwait(false);
        if (row.PartyId != partyId)
        {
            throw new DomainException(TaxErrors.AuthorizationNotCovered, "The fiscal authorization is of another customer.");
        }

        if (row.ValidUntil is { } until && until < AuthorizationStore.Today(context))
        {
            throw new DomainException(TaxErrors.AuthorizationExpired, $"The certificate was valid until {until:yyyy-MM-dd}; the sale carries ITBIS.");
        }

        AuthorizationStore.RequireStatus(row, "ACTIVE");
        var scope = await ScopeAsync(context, authorizationId, cancellationToken).ConfigureAwait(false);
        foreach (var group in lines.GroupBy(l => (l.ItemId, l.Uom)))
        {
            var line = scope.SingleOrDefault(s => s.ItemId == group.Key.ItemId && s.Uom == group.Key.Uom)
                ?? throw new DomainException(TaxErrors.AuthorizationNotCovered, "A line's product and unit are not in the authorization's scope; invoice them apart with ITBIS (E-FIS1-5).");
            if (group.Sum(l => l.Quantity) > line.QtyAvailable || group.Sum(l => l.Net) > line.NetAvailable)
            {
                throw new DomainException(
                    TaxErrors.AuthorizationExceeded,
                    $"The authorization has {line.QtyAvailable.ToString(System.Globalization.CultureInfo.InvariantCulture)} units and {line.NetAvailable:0.00} available for this product.");
            }
        }

        var (regime, certificate) = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT regime, certificate_no FROM tax.fiscal_authorization WHERE authorization_id = @id",
            r => new Tuple<string, string>(r.GetString(0), r.GetString(1)),
            cancellationToken,
            ("id", authorizationId)).ConfigureAwait(false))!;
        return new TaxExemption(authorizationId, regime, certificate);
    }

    /// <summary>E-FIS1-03-4: records the consumption of an issued invoice; a fully consumed scope makes the authorization EXHAUSTED.</summary>
    public static async Task ConsumeAsync(CommandContext context, Guid authorizationId, IReadOnlyList<CoveredLine> lines, Guid eventId, string commandType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);
        var scope = await ScopeAsync(context, authorizationId, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            var target = scope.Single(s => s.ItemId == line.ItemId && s.Uom == line.Uom);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO tax.fiscal_authorization_consumption (consumption_id, company_id, authorization_id, line_no, invoice_line_id, qty, net, event_id)
                VALUES (@id, @c, @a, @n, @il, @q, @v, @e)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("a", authorizationId),
                ("n", target.LineNo),
                ("il", line.InvoiceLineId),
                ("q", line.Quantity),
                ("v", line.Net),
                ("e", eventId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE tax.fiscal_authorization_line SET qty_consumed = qty_consumed + @q, net_consumed = net_consumed + @v WHERE authorization_id = @a AND line_no = @n",
                cancellationToken,
                ("q", line.Quantity),
                ("v", line.Net),
                ("a", authorizationId),
                ("n", target.LineNo)).ConfigureAwait(false);
        }

        if (await AuthorizationStore.ScalarAsync<bool?>(
                context,
                "SELECT bool_and(qty_consumed = qty_authorized OR net_consumed = net_authorized) FROM tax.fiscal_authorization_line WHERE authorization_id = @a",
                cancellationToken,
                ("a", authorizationId)).ConfigureAwait(false) is true)
        {
            var row = await AuthorizationStore.LockAsync(context, authorizationId, null, cancellationToken).ConfigureAwait(false);
            await AuthorizationStore.TransitionAsync(context, row, "EXHAUSTED", "FiscalAuthorizationExhausted", commandType, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// E-FIS1-03-7: returns consumption (a void returns everything still consumed; a credit note its net). Each release points to the
    /// invoice line's consumption and never passes what is still consumed; an EXHAUSTED authorization becomes ACTIVE again.
    /// </summary>
    public static async Task ReleaseAsync(CommandContext context, Guid authorizationId, IReadOnlyList<ReleasedLine> lines, Guid eventId, string commandType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);
        var row = await AuthorizationStore.LockAsync(context, authorizationId, null, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines.Where(l => l.Net > 0m))
        {
            var consumption = await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT c.consumption_id, c.line_no, c.qty - coalesce(sum(r.qty), 0), c.net - coalesce(sum(r.net), 0)
                FROM tax.fiscal_authorization_consumption c
                LEFT JOIN tax.fiscal_authorization_consumption r ON r.reverses_consumption_id = c.consumption_id
                WHERE c.authorization_id = @a AND c.invoice_line_id = @il AND c.reverses_consumption_id IS NULL
                GROUP BY c.consumption_id, c.line_no, c.qty, c.net
                """,
                r => new Tuple<Guid, int, decimal, decimal>(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3)),
                cancellationToken,
                ("a", authorizationId),
                ("il", line.InvoiceLineId)).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Invoice line {line.InvoiceLineId} consumed nothing of authorization {authorizationId}.");
            var (consumptionId, lineNo, qtyLeft, netLeft) = consumption;
            var qty = Math.Min(line.Quantity, qtyLeft);
            var net = Math.Min(line.Net, netLeft);
            if (net <= 0m)
            {
                continue;
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO tax.fiscal_authorization_consumption (consumption_id, company_id, authorization_id, line_no, invoice_line_id, qty, net, reverses_consumption_id, event_id)
                VALUES (@id, @c, @a, @n, @il, @q, @v, @r, @e)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("a", authorizationId),
                ("n", lineNo),
                ("il", line.InvoiceLineId),
                ("q", qty),
                ("v", net),
                ("r", consumptionId),
                ("e", eventId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE tax.fiscal_authorization_line SET qty_consumed = qty_consumed - @q, net_consumed = net_consumed - @v WHERE authorization_id = @a AND line_no = @n",
                cancellationToken,
                ("q", qty),
                ("v", net),
                ("a", authorizationId),
                ("n", lineNo)).ConfigureAwait(false);
        }

        if (row.Status == "EXHAUSTED")
        {
            await AuthorizationStore.TransitionAsync(context, row, "ACTIVE", "FiscalAuthorizationReopened", commandType, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<List<ScopeLine>> ScopeAsync(CommandContext context, Guid authorizationId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT line_no, item_id, uom, qty_authorized - qty_consumed, net_authorized - net_consumed
            FROM tax.fiscal_authorization_line WHERE authorization_id = @a ORDER BY line_no FOR UPDATE
            """,
            r => new ScopeLine(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4)),
            cancellationToken,
            ("a", authorizationId));
}
