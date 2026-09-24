using System.Net;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-PR18-4: read permissions, plant scope, paging and problems on the read side.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QueryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_plant_scoped_reader_sees_only_its_plant_and_must_say_which()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var p = await h.CreatePurchasingSetupAsync();
        var otherPlant = await h.CreatePlantAsync();
        var today = BusinessCalendar.DefaultBusinessDate(DateTime.UtcNow);
        var mine = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "po-1", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", 5m, 900m)]), new CreatePurchaseOrderHandler())).ResultRef;
        var theirs = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "po-2", otherPlant, p.SupplierId, today, [new(p.Sand, "t", 5m, 900m)]), new CreatePurchaseOrderHandler())).ResultRef;
        var user = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, user, "ALMACENISTA", p.PlantId);
        var storekeeper = await api.SignInAsync(user);
        var orders = $"/api/v1/companies/{h.CompanyId}/procurement/purchase-orders";

        var withoutPlant = await storekeeper.GetAsync(orders);
        var list = await storekeeper.GetOkAsync($"{orders}?plantId={p.PlantId}");
        var other = await storekeeper.GetAsync($"{orders}?plantId={otherPlant}");
        var ownDetail = await storekeeper.GetAsync($"{orders}/{mine}?plantId={p.PlantId}");
        var foreignDetail = await storekeeper.GetAsync($"{orders}/{theirs}?plantId={p.PlantId}");
        var plants = await storekeeper.GetOkAsync($"/api/v1/companies/{h.CompanyId}/master-data/plants?plantId={p.PlantId}");

        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await withoutPlant.ProblemAsync());
        Assert.Equal(mine, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("purchaseOrderId").GetGuid());
        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await other.ProblemAsync());
        Assert.Equal(HttpStatusCode.OK, ownDetail.StatusCode);
        Assert.Equal((HttpStatusCode.NotFound, "NOT_FOUND"), await foreignDetail.ProblemAsync());
        Assert.Equal(p.PlantId, Assert.Single(plants.GetProperty("items").EnumerateArray()).GetProperty("plantId").GetGuid());
    }

    [Fact]
    public async Task Company_wide_readers_list_master_data_with_decimals_as_strings()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var p = await h.CreatePurchasingSetupAsync();
        var buyer = await api.SignInAsSessionUserAsync(p.Buyer);

        var items = await buyer.GetOkAsync($"/api/v1/companies/{h.CompanyId}/master-data/items?status=ACTIVE");
        var suppliers = await buyer.GetOkAsync($"/api/v1/companies/{h.CompanyId}/master-data/suppliers");

        var sand = items.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "ARENA-LAVADA");
        Assert.Equal("m3>t:1.45000000", string.Join(',', sand.GetProperty("conversions").EnumerateArray().Select(c => $"{c.GetProperty("fromUom").GetString()}>{c.GetProperty("toUom").GetString()}:{c.GetProperty("factor").GetString()}")));
        Assert.Equal(2, items.GetProperty("items").GetArrayLength());
        Assert.Equal("Agregados del Este, S.R.L.|ACTIVE", string.Join(',', suppliers.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("legalName").GetString() + "|" + s.GetProperty("status").GetString())));
    }

    [Theory]
    [InlineData("procurement/purchase-orders?limit=0")]
    [InlineData("procurement/purchase-orders?limit=201")]
    [InlineData("procurement/purchase-orders?offset=-1")]
    [InlineData("reconciliation/periods?year=1999")]
    public async Task Bad_filters_are_400(string path)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var response = await controller.GetAsync($"/api/v1/companies/{h.CompanyId}/{path}");

        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_PARAMETER"), await response.ProblemAsync());
    }

    [Fact]
    public async Task Readers_without_the_permission_are_refused_and_missing_documents_are_404()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));
        var auditor = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("AUDITOR"));

        var invoices = await buyer.GetAsync($"/api/v1/companies/{h.CompanyId}/procurement/supplier-invoices");
        var explain = await buyer.GetAsync($"/api/v1/companies/{h.CompanyId}/finance/entries/{Guid.CreateVersion7()}/explanation");
        var missing = await auditor.GetAsync($"/api/v1/companies/{h.CompanyId}/procurement/supplier-invoices/{Guid.CreateVersion7()}");
        var periods = await auditor.GetOkAsync($"/api/v1/companies/{h.CompanyId}/reconciliation/periods?year=2026");

        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await invoices.ProblemAsync());
        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await explain.ProblemAsync());
        Assert.Equal((HttpStatusCode.NotFound, "NOT_FOUND"), await missing.ProblemAsync());
        Assert.Equal(2026, periods.GetProperty("year").GetInt32());
    }

    [Fact]
    public async Task Periods_show_component_states()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        await h.OpenPeriodsAsync(2026);
        await h.SetComponentAsync(new DateOnly(2026, 1, 15), "INV-MOV", "CLOSED");
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var periods = await controller.GetOkAsync($"/api/v1/companies/{h.CompanyId}/reconciliation/periods?year=2026");

        var items = periods.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(12, items.Count);
        Assert.Equal("2026-01-01", items[0].GetProperty("startsOn").GetString());
        Assert.Equal(
            "AP-REC:OPEN,INV-MOV:CLOSED",
            string.Join(',', items[0].GetProperty("components").EnumerateArray().Select(c => c.GetProperty("component").GetString() + ":" + c.GetProperty("status").GetString())));
    }
}
