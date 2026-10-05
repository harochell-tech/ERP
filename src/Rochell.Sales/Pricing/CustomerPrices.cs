using System.Data.Common;
using Rochell.Platform.Commands;
using Rochell.Sales.Orders;

namespace Rochell.Sales.Pricing;

/// <summary>
/// PRC-1 (E-PRC1-3, E-PRC1-7, E-PRS-02-4): where a document's prices come from — the version in force of the customer's own list
/// (from the customer's terms in force) and the version in force of GENERAL. A product missing from the customer's list takes
/// GENERAL's price; a customer whose list has no version in force, a customer without terms and the final consumer (E-PRC1-10) price
/// everything from GENERAL.
/// </summary>
internal static class CustomerPrices
{
    public sealed record Sources(Guid? Customer, Guid? General)
    {
        /// <summary>The version a document records as its list: the customer's own when it has one, else GENERAL's.</summary>
        public Guid Header => Customer ?? General ?? throw new DomainException(OrderErrors.PriceListMissing, "There is no approved price list.");
    }

    public static async Task<Sources> ResolveAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid? partyId, CancellationToken cancellationToken)
    {
        var general = await SalesSql.ScalarAsync<Guid?>(
            connection,
            transaction,
            """
            SELECT v.price_list_version_id FROM sal.price_list_version v JOIN sal.price_list l ON l.price_list_id = v.price_list_id
            WHERE l.company_id = @c AND l.code = 'GENERAL' AND v.status = 'ACTIVE'
            """,
            cancellationToken,
            ("c", companyId)).ConfigureAwait(false);
        var customer = partyId is not { } party ? null : await SalesSql.ScalarAsync<Guid?>(
            connection,
            transaction,
            """
            SELECT v.price_list_version_id
            FROM sal.customer_terms_version t
            JOIN sal.price_list l ON l.price_list_id = t.price_list_id AND l.code <> 'GENERAL'
            JOIN sal.price_list_version v ON v.price_list_id = l.price_list_id AND v.status = 'ACTIVE'
            WHERE t.company_id = @c AND t.party_id = @p AND t.status = 'ACTIVE'
            """,
            cancellationToken,
            ("c", companyId),
            ("p", party)).ConfigureAwait(false);
        var sources = new Sources(customer, general);
        _ = sources.Header; // no list in force at all refuses the document
        return sources;
    }

    /// <summary>The price of (item, unit) and the version it came from; missing from both lists refuses the document.</summary>
    public static async Task<(decimal Price, Guid VersionId)> PriceAsync(
        DbConnection connection, DbTransaction transaction, Sources sources, Guid itemId, string uom, CancellationToken cancellationToken)
    {
        foreach (var version in new[] { sources.Customer, sources.General })
        {
            if (version is { } v && await SalesSql.ScalarAsync<decimal?>(
                    connection, transaction, "SELECT unit_price FROM sal.price_list_line WHERE price_list_version_id = @l AND item_id = @i AND uom = @u", cancellationToken,
                    ("l", v), ("i", itemId), ("u", uom)).ConfigureAwait(false) is { } price)
            {
                return (price, v);
            }
        }

        throw new DomainException(OrderErrors.PriceMissing, $"Neither the customer's list nor GENERAL has a price for item {itemId} in {uom}.");
    }
}
