using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Sales.Queries;

// E-VS3-02-11: the master data of VS#3, read with sales:read.

public sealed record ListCustomers(Guid CompanyId, Guid SessionId, string? Status = null, string? Search = null, int Limit = 50, int Offset = 0) : IQuery;

/// <summary>A customer with the terms in force (null until approved).</summary>
public sealed record CustomerSummary(
    Guid PartyId, string? Rnc, string LegalName, string CustomerStatus, bool IsSupplier, string? Phone, string? Email, int? PaymentTermsDays, decimal? CreditLimit, bool? CreditHold, long Version);

public sealed record CustomerList(IReadOnlyList<CustomerSummary> Items, int Limit, int Offset);

[RequiresPermission("sales:read")]
public sealed class ListCustomersHandler : IQueryHandler<ListCustomers>
{
    public string QueryType => "Sales.ListCustomers";

    public async Task<string> HandleAsync(ListCustomers query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.party_id, p.rnc, p.legal_name, p.customer_status, p.is_supplier, p.phone, p.email,
                   t.payment_terms_days, t.credit_limit::numeric(19,2), t.credit_hold, p.version
            FROM md.party p
            LEFT JOIN sal.customer_terms_version t ON t.party_id = p.party_id AND t.status = 'ACTIVE'
            WHERE p.company_id = @c AND p.is_customer
              AND (CAST(@status AS text) IS NULL OR p.customer_status = CAST(@status AS text))
              AND (CAST(@search AS text) IS NULL OR p.legal_name ILIKE '%' || CAST(@search AS text) || '%' OR p.rnc LIKE CAST(@search AS text) || '%')
            ORDER BY p.legal_name, p.party_id
            LIMIT @limit OFFSET @offset
            """,
            r => new CustomerSummary(
                r.GetGuid(0), r.NullableString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.NullableString(5), r.NullableString(6),
                r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetBoolean(9), r.GetInt64(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("search", string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim()),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomerList(items, query.Limit, query.Offset));
    }
}

public sealed record GetCustomer(Guid CompanyId, Guid SessionId, Guid PartyId) : IQuery;

public sealed record CustomerTermsView(
    Guid TermsVersionId, Guid PartyId, string? CustomerName, int Version, DateOnly EffectiveFrom, int PaymentTermsDays, decimal CreditLimit, bool CreditHold, string Status,
    string? PreparedBy, string? ApprovedBy, Guid PriceListId, string PriceListCode, string PriceListName);

/// <remarks>E-IMP-6: <c>Emails</c> is the whole list; <c>Email</c> is its first entry.</remarks>
public sealed record CustomerDetail(
    Guid PartyId, string PartyKind, string? Rnc, string LegalName, string PartyStatus, string CustomerStatus, bool IsSupplier, string? Phone, string? Email, string? Address,
    long Version, IReadOnlyList<CustomerTermsView> Terms, IReadOnlyList<StateChange> History, IReadOnlyList<string> Emails);

internal static class TermsReading
{
    public const string Select = """
        SELECT t.terms_version_id, t.party_id, p.legal_name, t.version, t.effective_from, t.payment_terms_days, t.credit_limit::numeric(19,2), t.credit_hold, t.status,
               coalesce(pu.display_name, pu.email), coalesce(au.display_name, au.email), pl.price_list_id, pl.code, pl.name
        FROM sal.customer_terms_version t
        JOIN sal.price_list pl ON pl.price_list_id = t.price_list_id
        JOIN md.party p ON p.party_id = t.party_id
        JOIN iam.user pu ON pu.user_id = t.prepared_by
        LEFT JOIN iam.user au ON au.user_id = t.approved_by
        """;

    public static CustomerTermsView Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetGuid(1), r.NullableString(2), r.GetInt32(3), r.Date(4), r.GetInt32(5), r.GetDecimal(6), r.GetBoolean(7), r.GetString(8), r.NullableString(9), r.NullableString(10),
            r.GetGuid(11), r.GetString(12), r.GetString(13));
}

[RequiresPermission("sales:read")]
public sealed class GetCustomerHandler : IQueryHandler<GetCustomer>
{
    public string QueryType => "Sales.GetCustomer";

    private sealed record Head(string Kind, string? Rnc, string Name, string PartyStatus, string CustomerStatus, bool IsSupplier, string? Phone, string? Email, string? Address, long Version);

    public async Task<string> HandleAsync(GetCustomer query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var h = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT party_kind, rnc, legal_name, status::text, customer_status, is_supplier, phone, email, address, version
            FROM md.party WHERE company_id = @c AND party_id = @p AND is_customer
            """,
            r => new Head(r.GetString(0), r.NullableString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.NullableString(6), r.NullableString(7), r.NullableString(8), r.GetInt64(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The customer does not exist.");
        var terms = await Reading.ListAsync(
            context.Connection, context.Transaction, TermsReading.Select + " WHERE t.company_id = @c AND t.party_id = @p ORDER BY t.version DESC", TermsReading.Map, cancellationToken,
            ("c", context.CompanyId), ("p", query.PartyId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "Customer", query.PartyId, cancellationToken).ConfigureAwait(false);
        var emails = await Rochell.MasterData.Import.PartyEmails.ListAsync(context.Connection, context.Transaction, context.CompanyId, query.PartyId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomerDetail(query.PartyId, h.Kind, h.Rnc, h.Name, h.PartyStatus, h.CustomerStatus, h.IsSupplier, h.Phone, h.Email, h.Address, h.Version, terms, history, emails));
    }
}

/// <summary>Terms versions of every customer, e.g. the DRAFTs waiting for the Controller.</summary>
public sealed record ListCustomerTerms(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record CustomerTermsList(IReadOnlyList<CustomerTermsView> Items);

[RequiresPermission("sales:read")]
public sealed class ListCustomerTermsHandler : IQueryHandler<ListCustomerTerms>
{
    public string QueryType => "Sales.ListCustomerTerms";

    public async Task<string> HandleAsync(ListCustomerTerms query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            TermsReading.Select + " WHERE t.company_id = @c AND (CAST(@s AS text) IS NULL OR t.status = CAST(@s AS text)) ORDER BY p.legal_name, t.version DESC LIMIT 500",
            TermsReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomerTermsList(items));
    }
}

public sealed record ListStandardCosts(Guid CompanyId, Guid SessionId, string? Status = null, Guid? ItemId = null) : IQuery;

public sealed record StandardCostView(
    Guid CostVersionId, Guid ItemId, string ItemCode, string ItemDescription, string BaseUom, Guid ValuationAreaId, string ValuationAreaCode, int Version, DateOnly EffectiveFrom,
    decimal UnitCost, string Status, string? PreparedBy, string? ApprovedBy);

public sealed record StandardCostList(IReadOnlyList<StandardCostView> Items);

[RequiresPermission("sales:read")]
public sealed class ListStandardCostsHandler : IQueryHandler<ListStandardCosts>
{
    public string QueryType => "Sales.ListStandardCosts";

    public async Task<string> HandleAsync(ListStandardCosts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.cost_version_id, s.item_id, i.code, i.description, i.base_uom, s.valuation_area_id, a.code, s.version, s.effective_from, s.unit_cost, s.status, coalesce(pu.display_name, pu.email), coalesce(au.display_name, au.email)
            FROM md.standard_cost_version s
            JOIN md.item i ON i.item_id = s.item_id
            JOIN md.valuation_area a ON a.valuation_area_id = s.valuation_area_id
            JOIN iam.user pu ON pu.user_id = s.prepared_by
            LEFT JOIN iam.user au ON au.user_id = s.approved_by
            WHERE s.company_id = @c AND (CAST(@s AS text) IS NULL OR s.status = CAST(@s AS text)) AND (CAST(@i AS uuid) IS NULL OR s.item_id = CAST(@i AS uuid))
            ORDER BY i.code, a.code, s.version DESC
            LIMIT 500
            """,
            r => new StandardCostView(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetGuid(5), r.GetString(6), r.GetInt32(7), r.Date(8), r.GetDecimal(9), r.GetString(10),
                r.NullableString(11), r.NullableString(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status),
            ("i", query.ItemId)).ConfigureAwait(false);
        return ApiJson.Serialize(new StandardCostList(items));
    }
}

/// <summary>The versions of every list, or of <paramref name="PriceListId"/> (PRS-02).</summary>
public sealed record ListPriceLists(Guid CompanyId, Guid SessionId, Guid? PriceListId = null) : IQuery;

public sealed record PriceListSummary(
    Guid PriceListVersionId, int Version, DateOnly EffectiveFrom, string Status, int Lines, string? PreparedBy, string? ApprovedBy, Guid PriceListId, string PriceListCode,
    string PriceListName);

public sealed record PriceListList(IReadOnlyList<PriceListSummary> Items);

[RequiresPermission("sales:read")]
public sealed class ListPriceListsHandler : IQueryHandler<ListPriceLists>
{
    public string QueryType => "Sales.ListPriceLists";

    public async Task<string> HandleAsync(ListPriceLists query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.price_list_version_id, v.version, v.effective_from, v.status,
                   (SELECT count(*) FROM sal.price_list_line l WHERE l.price_list_version_id = v.price_list_version_id)::int, coalesce(pu.display_name, pu.email), coalesce(au.display_name, au.email),
                   pl.price_list_id, pl.code, pl.name
            FROM sal.price_list_version v
            JOIN sal.price_list pl ON pl.price_list_id = v.price_list_id
            JOIN iam.user pu ON pu.user_id = v.prepared_by
            LEFT JOIN iam.user au ON au.user_id = v.approved_by
            WHERE v.company_id = @c AND (@l::uuid IS NULL OR v.price_list_id = @l)
            ORDER BY pl.code <> 'GENERAL', pl.code, v.version DESC
            """,
            r => new PriceListSummary(r.GetGuid(0), r.GetInt32(1), r.Date(2), r.GetString(3), r.GetInt32(4), r.NullableString(5), r.NullableString(6), r.GetGuid(7), r.GetString(8), r.GetString(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.PriceListId)).ConfigureAwait(false);
        return ApiJson.Serialize(new PriceListList(items));
    }
}

/// <summary>E-PRC1-11: the named lists — status, the version in force, how many customers have each in their terms in force.</summary>
public sealed record ListPriceListHeaders(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record PriceListHeaderView(
    Guid PriceListId, string Code, string Name, string Status, long Version, Guid? ActiveVersionId, int? ActiveVersion, DateOnly? ActiveFrom, bool HasDraft, int Customers);

public sealed record PriceListHeaderList(IReadOnlyList<PriceListHeaderView> Items);

[RequiresPermission("sales:read")]
public sealed class ListPriceListHeadersHandler : IQueryHandler<ListPriceListHeaders>
{
    public string QueryType => "Sales.ListPriceListHeaders";

    public async Task<string> HandleAsync(ListPriceListHeaders query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.price_list_id, l.code, l.name, l.status, l.version, a.price_list_version_id, a.version, a.effective_from,
                   EXISTS (SELECT 1 FROM sal.price_list_version d WHERE d.price_list_id = l.price_list_id AND d.status = 'DRAFT'),
                   (SELECT count(*) FROM sal.customer_terms_version t WHERE t.price_list_id = l.price_list_id AND t.status = 'ACTIVE')::int
            FROM sal.price_list l
            LEFT JOIN sal.price_list_version a ON a.price_list_id = l.price_list_id AND a.status = 'ACTIVE'
            WHERE l.company_id = @c
            ORDER BY l.code <> 'GENERAL', l.code
            """,
            r => new PriceListHeaderView(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.NullableGuid(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.Date(7),
                r.GetBoolean(8), r.GetInt32(9)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new PriceListHeaderList(items));
    }
}

public sealed record GetPriceList(Guid CompanyId, Guid SessionId, Guid PriceListVersionId) : IQuery;

public sealed record PriceListLineView(Guid ItemId, string ItemCode, string ItemDescription, string Uom, decimal UnitPrice);

/// <summary>E-PRS-03-6: a freight price of the version — product, unit, zone, price per unit of the product.</summary>
public sealed record PriceListFreightView(Guid ItemId, string ItemCode, string ItemDescription, string Uom, Guid ZoneId, string ZoneName, decimal UnitPrice);

public sealed record PriceListDetail(PriceListSummary Header, IReadOnlyList<PriceListLineView> Lines, IReadOnlyList<PriceListFreightView> Freight);

[RequiresPermission("sales:read")]
public sealed class GetPriceListHandler : IQueryHandler<GetPriceList>
{
    public string QueryType => "Sales.GetPriceList";

    public async Task<string> HandleAsync(GetPriceList query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var list = System.Text.Json.JsonSerializer.Deserialize<PriceListList>(
            await new ListPriceListsHandler().HandleAsync(new ListPriceLists(query.CompanyId, query.SessionId), context, cancellationToken).ConfigureAwait(false), ApiJson.Options)!;
        var header = list.Items.SingleOrDefault(i => i.PriceListVersionId == query.PriceListVersionId)
            ?? throw new DomainException(QueryErrors.NotFound, "The price list does not exist.");
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.item_id, i.code, i.description, l.uom, l.unit_price
            FROM sal.price_list_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.price_list_version_id = @id ORDER BY i.code, l.uom
            """,
            r => new PriceListLineView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4)),
            cancellationToken,
            ("id", query.PriceListVersionId)).ConfigureAwait(false);
        var freight = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.item_id, i.code, i.description, f.uom, f.zone_id, z.name, f.unit_price
            FROM sal.price_list_freight f JOIN md.item i ON i.item_id = f.item_id JOIN sal.delivery_zone z ON z.zone_id = f.zone_id
            WHERE f.price_list_version_id = @id ORDER BY i.code, f.uom, lower(z.name)
            """,
            r => new PriceListFreightView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetDecimal(6)),
            cancellationToken,
            ("id", query.PriceListVersionId)).ConfigureAwait(false);
        return ApiJson.Serialize(new PriceListDetail(header, lines, freight));
    }
}

public sealed record ListVehicles(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

/// <summary>E-FLT-1, E-FLT-2: <see cref="FleetCode"/> is the «ficha» ("BR 09"); null on vehicles registered before it existed (E-FLT-5).</summary>
public sealed record VehicleView(Guid VehicleId, string Plate, decimal CapacityKg, string Status, long Version, string? FleetCode, string? InsurancePolicyNo);

public sealed record VehicleList(IReadOnlyList<VehicleView> Items);

[RequiresPermission("sales:read")]
public sealed class ListVehiclesHandler : IQueryHandler<ListVehicles>
{
    public string QueryType => "Sales.ListVehicles";

    public async Task<string> HandleAsync(ListVehicles query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT vehicle_id, plate, capacity_kg, status, version, fleet_code, insurance_policy_no FROM log.vehicle WHERE company_id = @c AND (CAST(@s AS text) IS NULL OR status = CAST(@s AS text)) ORDER BY fleet_code NULLS LAST, plate",
            r => new VehicleView(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetString(3), r.GetInt64(4), r.NullableString(5), r.NullableString(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new VehicleList(items));
    }
}

public sealed record ListDrivers(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

/// <summary>
/// E-FLT-3, E-FLT-4: <see cref="DaysToLicenseExpiry"/> = licence expiry − today's business date (0 on its last day, negative once past);
/// null without a date. An expired licence only warns.
/// </summary>
public sealed record DriverView(Guid DriverId, string FullName, string NationalId, string Status, long Version, DateOnly? LicenseExpiresOn, int? DaysToLicenseExpiry);

public sealed record DriverList(IReadOnlyList<DriverView> Items);

[RequiresPermission("sales:read")]
public sealed class ListDriversHandler : IQueryHandler<ListDrivers>
{
    public string QueryType => "Sales.ListDrivers";

    public async Task<string> HandleAsync(ListDrivers query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT driver_id, full_name, national_id, status, version, license_expires_on, license_expires_on - CAST(@today AS date)
            FROM log.driver WHERE company_id = @c AND (CAST(@s AS text) IS NULL OR status = CAST(@s AS text)) ORDER BY full_name
            """,
            r => new DriverView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.IsDBNull(5) ? null : r.Date(5), r.IsDBNull(6) ? null : r.GetInt32(6)),
            cancellationToken,
            ("today", BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow)),
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new DriverList(items));
    }
}

public sealed record ListSalesPlants(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record SalesLocationView(Guid LocationId, string Code);

public sealed record SalesPlantView(Guid PlantId, string Code, Guid ValuationAreaId, IReadOnlyList<SalesLocationView> Locations, string? Name = null);

public sealed record SalesPlantList(IReadOnlyList<SalesPlantView> Items);

/// <summary>
/// E-VS3-10-13: the plants and their stock locations (not the transit one) for the sales and dispatch screens, read with sales:read
/// so the Vendedor and Despacho need no master_data:read.
/// </summary>
[RequiresPermission("sales:read")]
public sealed class ListSalesPlantsHandler : IQueryHandler<ListSalesPlants>
{
    public string QueryType => "Sales.ListSalesPlants";

    public async Task<string> HandleAsync(ListSalesPlants query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var plants = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, code, valuation_area_id, name FROM md.plant WHERE company_id = @c ORDER BY code",
            r => new SalesPlantView(r.GetGuid(0), r.GetString(1), r.GetGuid(2), [], r.NullableString(3)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var locations = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, location_id, code FROM md.location WHERE company_id = @c AND NOT is_transit AND NOT is_curing ORDER BY code", // E-MFG1-04-5
            r => (PlantId: r.GetGuid(0), View: new SalesLocationView(r.GetGuid(1), r.GetString(2))),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false))
            .ToLookup(l => l.PlantId, l => l.View);
        return ApiJson.Serialize(new SalesPlantList(plants.Select(p => p with { Locations = locations[p.PlantId].ToList() }).ToList()));
    }
}

public sealed record ListSalesBankAccounts(Guid CompanyId, Guid SessionId) : IQuery;

/// <summary>E-UX4-6: <see cref="Alias"/> is the account's short name, if any.</summary>
public sealed record SalesBankAccountView(Guid BankAccountId, string BankCode, string AccountNumber, string? Alias);

public sealed record SalesBankAccountList(IReadOnlyList<SalesBankAccountView> Items);

/// <summary>
/// E-VS3-10-14: the company's ACTIVE bank accounts, number masked, for the receipt and deposit forms, read with sales:read so Cobros
/// needs no bank:read (statements, supplier payments and full numbers stay with Treasury).
/// </summary>
[RequiresPermission("sales:read")]
public sealed class ListSalesBankAccountsHandler : IQueryHandler<ListSalesBankAccounts>
{
    public string QueryType => "Sales.ListSalesBankAccounts";

    public async Task<string> HandleAsync(ListSalesBankAccounts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT bank_account_id, bank_code, account_number, alias FROM fin.bank_account WHERE company_id = @c AND status = 'ACTIVE' ORDER BY bank_code, account_number",
            r => new SalesBankAccountView(r.GetGuid(0), r.GetString(1), AccountNumbers.Show(r.GetString(2), full: false), r.NullableString(3)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new SalesBankAccountList(items));
    }
}
