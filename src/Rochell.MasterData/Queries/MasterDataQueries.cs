using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.MasterData.Queries;

// E-PR18-4: master data read by the roles that pick it in a document. Suppliers and items are company data; a plant-scoped
// reader passes its plant only to be authorized (the plant list is then that plant alone).

public sealed record ListSuppliers(Guid CompanyId, Guid SessionId, Guid? PlantId = null, string? Status = null, int Limit = 50, int Offset = 0) : IPlantScopedQuery;

/// <summary>
/// E-UI-4: <see cref="BankAccountState"/> is PAYABLE (verified, past its 72 h), HOLD_PENDING (verified, within them), REVIEW (a version
/// waits for verification, none verified) or NONE; <see cref="OpenApAmount"/> sums the open AP documents of its posted invoices.
/// </summary>
/// <summary>E-VS3-02-10: <see cref="PaymentTermsDays"/> proposes the due date of the supplier's invoices on screen.</summary>
/// <remarks>
/// E-IMP-6, E-IMP-01-2: <c>Phone</c> and <c>Emails</c> (the first is the principal one) are the supplier's contact data. E-USD1-03-9: a
/// FOREIGN supplier has <c>Country</c> and optionally <c>ForeignTaxId</c>, no RNC.
/// </remarks>
public sealed record SupplierView(
    Guid SupplierId, string PartyKind, string? Rnc, string LegalName, string Status, DateTime? RncValidatedAt, long Version, string BankAccountState, decimal OpenApAmount, int? PaymentTermsDays,
    string? Phone, IReadOnlyList<string> Emails, string? Country = null, string? ForeignTaxId = null);

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
            SELECT p.party_id, p.party_kind, p.rnc, p.legal_name, p.status::text, p.rnc_validated_at, p.version,
                   CASE WHEN EXISTS (SELECT 1 FROM md.party_bank_account v WHERE v.party_id = p.party_id AND v.status = 'VERIFIED' AND v.payable_from <= @now) THEN 'PAYABLE'
                        WHEN EXISTS (SELECT 1 FROM md.party_bank_account v WHERE v.party_id = p.party_id AND v.status = 'VERIFIED') THEN 'HOLD_PENDING'
                        WHEN EXISTS (SELECT 1 FROM md.party_bank_account v WHERE v.party_id = p.party_id AND v.status = 'REVIEW') THEN 'REVIEW'
                        ELSE 'NONE' END,
                   (SELECT coalesce(sum(d.open_amount), 0) FROM fin.ap_document d
                    JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id AND i.accounting_status::text = 'POSTED'
                    WHERE d.party_id = p.party_id),
                   p.supplier_payment_terms_days, p.phone,
                   ARRAY(SELECT e.email FROM md.party_email e WHERE e.company_id = p.company_id AND e.party_id = p.party_id ORDER BY e.position),
                   p.country, p.foreign_tax_id
            FROM md.party p
            WHERE p.company_id = @c AND p.is_supplier AND (CAST(@status AS text) IS NULL OR p.status::text = CAST(@status AS text))
            ORDER BY p.legal_name, p.party_id
            LIMIT @limit OFFSET @offset
            """,
            r => new SupplierView(r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.NullableUtc(5), r.GetInt64(6), r.GetString(7), r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.NullableString(10), r.GetFieldValue<string[]>(11),
                r.IsDBNull(12) ? null : r.GetString(12), r.NullableString(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("now", context.Clock.UtcNow),
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

/// <param name="IncludeInactive">E-PLT-3: inactive plants and locations are left out unless asked for (the plants screen asks).</param>
public sealed record ListPlants(Guid CompanyId, Guid SessionId, Guid? PlantId = null, bool IncludeInactive = false) : IPlantScopedQuery;

/// <param name="Name">E-PLT-2: the location's readable name.</param>
public sealed record LocationView(Guid LocationId, string Code, string? Name = null, string Status = "ACTIVE");

/// <param name="Name">E-UX1-01-4: the readable name, null until one is set.</param>
public sealed record PlantView(
    Guid PlantId, string Code, Guid ValuationAreaId, string ValuationAreaCode, IReadOnlyList<LocationView> Locations, string? Name = null, string Status = "ACTIVE");

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
            SELECT p.plant_id, p.code, p.valuation_area_id, va.code, p.name, p.status
            FROM md.plant p
            JOIN md.valuation_area va ON va.valuation_area_id = p.valuation_area_id
            WHERE p.company_id = @c AND (CAST(@plant AS uuid) IS NULL OR p.plant_id = CAST(@plant AS uuid)) AND (@all OR p.status = 'ACTIVE')
            ORDER BY p.code
            """,
            r => new PlantView(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), [], r.NullableString(4), r.GetString(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("plant", query.PlantId),
            ("all", query.IncludeInactive)).ConfigureAwait(false);

        var locations = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, location_id, code, name, status FROM md.location WHERE company_id = @c AND (@all OR status = 'ACTIVE') ORDER BY code",
            r => (PlantId: r.GetGuid(0), View: new LocationView(r.GetGuid(1), r.GetString(2), r.NullableString(3), r.GetString(4))),
            cancellationToken,
            ("c", context.CompanyId),
            ("all", query.IncludeInactive)).ConfigureAwait(false))
            .ToLookup(l => l.PlantId, l => l.View);

        return ApiJson.Serialize(new PlantList(plants.Select(p => p with { Locations = locations[p.PlantId].ToList() }).ToList()));
    }
}

/// <summary>
/// E-UX3-11: the units of measure (<c>md.uom</c>): code and dimension (MASS, VOLUME, COUNT); the catalogue has no name column. A
/// plant-scoped reader passes its plant only to be authorized.
/// </summary>
public sealed record ListUoms(Guid CompanyId, Guid SessionId, Guid? PlantId = null) : IPlantScopedQuery;

public sealed record UomView(string Code, string Dimension);

public sealed record UomList(IReadOnlyList<UomView> Items);

[RequiresPermission("master_data:read")]
public sealed class ListUomsHandler : IQueryHandler<ListUoms>
{
    public string QueryType => "MasterData.ListUoms";

    public async Task<string> HandleAsync(ListUoms query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT uom_code, dimension FROM md.uom ORDER BY dimension, uom_code",
            r => new UomView(r.GetString(0), r.GetString(1)),
            cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new UomList(items));
    }
}
