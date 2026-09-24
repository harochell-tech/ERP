using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.MasterData.Queries;

// E-PR18-4: master data read by the roles that pick it in a document. Suppliers and items are company data; a plant-scoped
// reader passes its plant only to be authorized (the plant list is then that plant alone).

public sealed record ListSuppliers(Guid CompanyId, Guid SessionId, Guid? PlantId = null, string? Status = null, int Limit = 50, int Offset = 0) : IPlantScopedQuery;

public sealed record SupplierView(Guid SupplierId, string PartyKind, string? Rnc, string LegalName, string Status, DateTime? RncValidatedAt, long Version);

public sealed record SupplierList(IReadOnlyList<SupplierView> Items, int Limit, int Offset);

[RequiresPermission("master_data:read")]
public sealed class ListSuppliersHandler : IQueryHandler<ListSuppliers>
{
    public string QueryType => "MasterData.ListSuppliers";

    public async Task<string> HandleAsync(ListSuppliers query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT party_id, party_kind, rnc, legal_name, status::text, rnc_validated_at, version
            FROM md.party
            WHERE company_id = @c AND is_supplier AND (CAST(@status AS text) IS NULL OR status::text = CAST(@status AS text))
            ORDER BY legal_name, party_id
            LIMIT @limit OFFSET @offset
            """,
            r => new SupplierView(r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.NullableUtc(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new SupplierList(items, query.Limit, query.Offset));
    }
}

public sealed record ListItems(Guid CompanyId, Guid SessionId, Guid? PlantId = null, string? Status = null, int Limit = 50, int Offset = 0) : IPlantScopedQuery;

public sealed record UomConversionView(string FromUom, string ToUom, decimal Factor, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record ItemView(
    Guid ItemId,
    string Code,
    string Description,
    string ItemType,
    string BaseUom,
    string ItemCategory,
    string Status,
    long Version,
    IReadOnlyList<UomConversionView> Conversions);

public sealed record ItemList(IReadOnlyList<ItemView> Items, int Limit, int Offset);

[RequiresPermission("master_data:read")]
public sealed class ListItemsHandler : IQueryHandler<ListItems>
{
    public string QueryType => "MasterData.ListItems";

    public async Task<string> HandleAsync(ListItems query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT item_id, code, description, item_type, base_uom, item_category, status::text, version
            FROM md.item
            WHERE company_id = @c AND (CAST(@status AS text) IS NULL OR status::text = CAST(@status AS text))
            ORDER BY code, item_id
            LIMIT @limit OFFSET @offset
            """,
            r => new ItemView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), []),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);

        var ids = items.Select(i => i.ItemId).ToArray();
        var conversions = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT item_id, from_uom, to_uom, factor, effective_from, effective_to
            FROM md.uom_conversion
            WHERE company_id = @c AND item_id = ANY(@ids)
            ORDER BY item_id, from_uom, to_uom, effective_from
            """,
            r => (ItemId: r.GetGuid(0), View: new UomConversionView(r.GetString(1), r.GetString(2), r.GetDecimal(3), r.Date(4), r.IsDBNull(5) ? null : r.Date(5))),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", ids)).ConfigureAwait(false))
            .ToLookup(c => c.ItemId, c => c.View);

        return ApiJson.Serialize(new ItemList(items.Select(i => i with { Conversions = conversions[i.ItemId].ToList() }).ToList(), query.Limit, query.Offset));
    }
}

public sealed record ListPlants(Guid CompanyId, Guid SessionId, Guid? PlantId = null) : IPlantScopedQuery;

public sealed record LocationView(Guid LocationId, string Code);

public sealed record PlantView(Guid PlantId, string Code, Guid ValuationAreaId, string ValuationAreaCode, IReadOnlyList<LocationView> Locations);

public sealed record PlantList(IReadOnlyList<PlantView> Items);

[RequiresPermission("master_data:read")]
public sealed class ListPlantsHandler : IQueryHandler<ListPlants>
{
    public string QueryType => "MasterData.ListPlants";

    public async Task<string> HandleAsync(ListPlants query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var plants = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.plant_id, p.code, p.valuation_area_id, va.code
            FROM md.plant p
            JOIN md.valuation_area va ON va.valuation_area_id = p.valuation_area_id
            WHERE p.company_id = @c AND (CAST(@plant AS uuid) IS NULL OR p.plant_id = CAST(@plant AS uuid))
            ORDER BY p.code
            """,
            r => new PlantView(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), []),
            cancellationToken,
            ("c", context.CompanyId),
            ("plant", query.PlantId)).ConfigureAwait(false);

        var locations = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, location_id, code FROM md.location WHERE company_id = @c ORDER BY code",
            r => (PlantId: r.GetGuid(0), View: new LocationView(r.GetGuid(1), r.GetString(2))),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false))
            .ToLookup(l => l.PlantId, l => l.View);

        return ApiJson.Serialize(new PlantList(plants.Select(p => p with { Locations = locations[p.PlantId].ToList() }).ToList()));
    }
}
