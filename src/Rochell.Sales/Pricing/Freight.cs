using System.Data.Common;
using Rochell.Platform.Commands;
using Rochell.Tax;

namespace Rochell.Sales.Pricing;

/// <summary>
/// SRV-1 (E-SRV1-3/12/17, E-PRS-04-1…3): the freight of an own-truck document to a zone. Each product line takes the freight price of
/// its (item, unit, zone) from the customer's own list in force — never GENERAL as a fallback, but GENERAL when it is the customer's
/// list or the document is a cash sale. Freight is added only when it can be invoiced exempt and posted: the freight item is ACTIVE,
/// the SALES_ITBIS rule in force charges no ITBIS on TRANSPORTE, and P-16 in force has its freight line with FREIGHT_REVENUE mapped.
/// Otherwise the lines go without freight and <see cref="Result.Withheld"/> says why.
/// </summary>
internal static class Freight
{
    public const string ItemMissing = FreightReasons.ItemMissing;
    public const string NotExempt = FreightReasons.NotExempt;
    public const string PostingMissing = FreightReasons.PostingMissing;

    /// <summary>The freight unit price of each (item, unit) that has one, and the reason freight was withheld, if it was.</summary>
    public sealed record Result(IReadOnlyDictionary<(Guid ItemId, string Uom), decimal> Prices, string? Withheld)
    {
        public static readonly Result None = new(new Dictionary<(Guid, string), decimal>(), null);
    }

    /// <summary>The version in force of the customer's own list (GENERAL's for a customer on GENERAL, without terms, or a cash sale).</summary>
    public static async Task<Guid?> OwnVersionAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid? partyId, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<Guid?>(
            connection,
            transaction,
            """
            SELECT v.price_list_version_id
            FROM sal.price_list l
            JOIN sal.price_list_version v ON v.price_list_id = l.price_list_id AND v.status = 'ACTIVE'
            WHERE l.company_id = @c AND l.price_list_id = coalesce(
                (SELECT t.price_list_id FROM sal.customer_terms_version t WHERE t.company_id = @c AND t.party_id = @p AND t.status = 'ACTIVE'),
                (SELECT g.price_list_id FROM sal.price_list g WHERE g.company_id = @c AND g.code = 'GENERAL'))
            """,
            cancellationToken,
            ("c", companyId),
            ("p", partyId)).ConfigureAwait(false);

    public static async Task<Result> PriceAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId, Guid? partyId, Guid? zoneId, bool exemptionPending, DateOnly date,
        IEnumerable<(Guid ItemId, string Uom)> lines, CancellationToken cancellationToken)
    {
        if (zoneId is not { } zone || exemptionPending)
        {
            return Result.None;
        }

        if (await OwnVersionAsync(connection, transaction, companyId, partyId, cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return Result.None;
        }

        var prices = new Dictionary<(Guid, string), decimal>();
        foreach (var (itemId, uom) in lines.Distinct())
        {
            if (await SalesSql.ScalarAsync<decimal?>(
                    connection, transaction, "SELECT unit_price FROM sal.price_list_freight WHERE price_list_version_id = @v AND item_id = @i AND uom = @u AND zone_id = @z",
                    cancellationToken, ("v", version), ("i", itemId), ("u", uom), ("z", zone)).ConfigureAwait(false) is { } price)
            {
                prices[(itemId, uom)] = price;
            }
        }

        if (prices.Count == 0)
        {
            return Result.None;
        }

        var withheld = await WithheldAsync(connection, transaction, companyId, date, cancellationToken).ConfigureAwait(false);
        return withheld is null ? new Result(prices, null) : new Result(new Dictionary<(Guid, string), decimal>(), withheld);
    }

    /// <summary>Why freight cannot ride a document today (E-PRS-04-1/2/3), or null when it can.</summary>
    public static async Task<string?> WithheldAsync(DbConnection connection, DbTransaction transaction, Guid companyId, DateOnly date, CancellationToken cancellationToken)
    {
        if (await ItemAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ItemMissing;
        }

        try
        {
            var taxes = await TaxEngine.PreviewSalesItbisAsync(
                connection, transaction, companyId, date, [new TaxableLine(Guid.Empty, "TRANSPORTE", 1m)], cancellationToken).ConfigureAwait(false);
            if (taxes.Any(t => t.Amount != 0m))
            {
                return NotExempt;
            }
        }
        catch (DomainException)
        {
            return NotExempt;
        }

        var posting = await SalesSql.ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT EXISTS (
                     SELECT 1 FROM fin.posting_rule_version v JOIN fin.posting_rule r ON r.posting_rule_id = v.posting_rule_id
                     WHERE r.code = 'P-16' AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
                       AND v.definition -> 'lines' @> '[{"code": "P16-CR-FRT"}]'::jsonb)
               AND EXISTS (
                     SELECT 1 FROM fin.account_role_map m
                     WHERE m.company_id = @c AND m.account_role = 'FREIGHT_REVENUE' AND m.status = 'ACTIVE'
                       AND m.effective_from <= @d AND (m.effective_to IS NULL OR m.effective_to > @d))
            """,
            cancellationToken,
            ("c", companyId),
            ("d", date)).ConfigureAwait(false);
        return posting ? null : PostingMissing;
    }

    /// <summary>The company's ACTIVE freight item (E-PRS-01-7).</summary>
    public static Task<Guid?> ItemAsync(DbConnection connection, DbTransaction transaction, Guid companyId, CancellationToken cancellationToken)
        => SalesSql.ScalarAsync<Guid?>(
            connection, transaction, "SELECT item_id FROM md.item WHERE company_id = @c AND item_category = 'TRANSPORTE' AND status = 'ACTIVE'", cancellationToken, ("c", companyId));

    public static decimal Amount(decimal quantity, decimal unitPrice) => decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Why a document goes without freight (<c>freightWithheld</c>), and the error of a freight item missing at invoicing.</summary>
public static class FreightReasons
{
    public const string ItemMissing = "FREIGHT_ITEM_MISSING";
    public const string NotExempt = "FREIGHT_NOT_EXEMPT";
    public const string PostingMissing = "FREIGHT_POSTING_MISSING";
}
