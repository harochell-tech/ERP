using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.Queries;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>UX4-01: sequential numbers (E-UX4-5), PO totals and preview (E-UX4-2/3), receiving location (E-UX4-8), printed total (E-UX4-7).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4ProcurementTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static Task<CommandResult> Receive(TestHarness h, TestInvoicing s, string key, Guid location, decimal quantity = 1m)
        => h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, s.Receiving.Storekeeper, key, s.Purchasing.PlantId, s.PurchaseOrderId, location, h.Clock.UtcNow.AddMinutes(-1), [new(s.PoLineId, quantity)]),
            new PostGoodsReceiptHandler());

    [Fact]
    public async Task Orders_and_receipts_are_numbered_per_company_and_year_ignoring_earlier_numbers()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync(); // one order, one receipt
        var p = s.Purchasing;
        var year = Today(h).Year;

        // An order numbered the earlier way (8 characters, here all digits) does not move the sequence.
        await h.AdminRequireAsync(
            $"""
            BEGIN; SET LOCAL session_replication_role = replica;
            INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version)
            VALUES (gen_random_uuid(), '{h.CompanyId}', 'OC-{year}-12345678', '{p.SupplierId}', '{p.PlantId}', current_date, 'DRAFT', '{h.UserId}', 1);
            COMMIT;
            """);
        var second = await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "po-2", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 1m, 100m)]), new CreatePurchaseOrderHandler());
        await Receive(h, s, "gr-2", s.Receiving.LocationB);

        Assert.Equal($"OC-{year}-000001|RM-{year}-000001", await h.ScalarAsync<string>(
            "SELECT (SELECT po_no FROM pur.purchase_order WHERE po_id = @po) || '|' || (SELECT gr_no FROM pur.goods_receipt WHERE gr_id = @gr)", ("po", s.PurchaseOrderId), ("gr", s.GoodsReceiptId)));
        Assert.Equal($"OC-{year}-000002", JsonDocument.Parse(second.ResultPayload).RootElement.GetProperty("poNo").GetString());
        Assert.Equal($"RM-{year}-000001,RM-{year}-000002", await h.ScalarAsync<string>("SELECT string_agg(gr_no, ',' ORDER BY gr_no) FROM pur.goods_receipt"));
    }

    [Fact]
    public async Task Order_list_and_detail_carry_the_net_total()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync(); // 10 t × 1 500
        var p = s.Purchasing;
        await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "po-2", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 2.5m, 100.555m), new(p.Cement, "t", 3m, 350m)]),
            new CreatePurchaseOrderHandler());

        var list = JsonDocument.Parse(await h.QueryAsync(new ListPurchaseOrders(h.CompanyId, p.Buyer, p.PlantId, SupplierId: p.SupplierId), new ListPurchaseOrdersHandler())).RootElement;
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetPurchaseOrder(h.CompanyId, p.Buyer, s.PurchaseOrderId, p.PlantId), new GetPurchaseOrderHandler())).RootElement;

        // 10 × 1 500 = 15 000.00; 2.5 × 100.555 = 251.3875 → 251.39, + 3 × 350 = 1 050.00 → 1 301.39.
        Assert.Equal("1301.39,15000.00", string.Join(',', list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("total").GetString()).Order(StringComparer.Ordinal)));
        Assert.Equal(("15000.00", "15000.00"), (detail.GetProperty("total").GetString(), detail.GetProperty("lines")[0].GetProperty("netAmount").GetString()));
    }

    [Fact]
    public async Task Order_preview_prices_the_draft_and_estimates_ITBIS_without_writing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var p = s.Purchasing;
        PurchaseOrderLineInput[] lines = [new(p.Sand, "t", 2m, 1500.555m), new(p.Sand, "t", 0.5m, 999.99m)];
        var preview = new PreviewPurchaseOrder(h.CompanyId, p.Buyer, p.PlantId, p.SupplierId, Today(h), lines);

        var closed = JsonDocument.Parse(await h.QueryAsync(preview, new PreviewPurchaseOrderHandler())).RootElement;
        await h.EnableInvoicePostingAsync(); // ITBIS-COMPRAS 18 % in force
        var (orders, commands) = (await h.CountAsync("pur.purchase_order"), await h.CountAsync("core.command_log"));
        var open = JsonDocument.Parse(await h.QueryAsync(preview, new PreviewPurchaseOrderHandler())).RootElement;
        var invalid = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(preview with { Lines = [new(p.Sand, "t", 1m, 0m)] }, new PreviewPurchaseOrderHandler()));

        // 2 × 1 500.555 = 3 001.11; 0.5 × 999.99 = 499.995 → 500.00 (half away from zero); net 3 501.11.
        // ITBIS 18 %: 540.1998 → 540.20 and 90.00; total 630.20; gross 4 131.31.
        Assert.Equal(("3501.11", JsonValueKind.Null, TaxErrors.FiscalGateClosed), (closed.GetProperty("netTotal").GetString(), closed.GetProperty("itbisTotal").ValueKind,
            closed.GetProperty("itbisUnavailableCode").GetString()));
        Assert.Equal("3001.11/540.20,500.00/90.00", string.Join(',', open.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("netAmount").GetString()}/{l.GetProperty("itbis").GetString()}")));
        Assert.Equal(("3501.11", "630.20", "4131.31", JsonValueKind.Null), (open.GetProperty("netTotal").GetString(), open.GetProperty("itbisTotal").GetString(),
            open.GetProperty("total").GetString(), open.GetProperty("itbisUnavailableCode").ValueKind));
        Assert.Equal(ProcurementErrors.PriceInvalid, invalid.Code);
        Assert.Equal((orders, commands), (await h.CountAsync("pur.purchase_order"), await h.CountAsync("core.command_log")));
    }

    [Fact]
    public async Task Raw_material_is_not_received_into_CURADO_or_TRANSITO_and_RECEPCION_is_the_default()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync(); // locations PATIO-A and PATIO-B
        var plant = s.Purchasing.PlantId;
        var curado = Guid.CreateVersion7();
        var transito = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.location (location_id, company_id, plant_id, code, is_curing) VALUES ('{curado}', '{h.CompanyId}', '{plant}', 'CURADO', true);
            INSERT INTO md.location (location_id, company_id, plant_id, code, is_transit) VALUES ('{transito}', '{h.CompanyId}', '{plant}', 'TRANSITO', true);
            """);

        async Task<JsonElement> Order() => JsonDocument.Parse(await h.QueryAsync(new ListPurchaseOrdersToReceive(h.CompanyId, s.Receiving.Storekeeper, plant), new ListPurchaseOrdersToReceiveHandler()))
            .RootElement.GetProperty("items").EnumerateArray().Single(o => o.GetProperty("purchaseOrderId").GetGuid() == s.PurchaseOrderId);

        var intoCuring = await Assert.ThrowsAsync<DomainException>(() => Receive(h, s, "gr-c", curado));
        var intoTransit = await Assert.ThrowsAsync<DomainException>(() => Receive(h, s, "gr-t", transito));
        var twoPatios = await Order();
        var recepcion = await h.CreateLocationAsync(plant, "RECEPCION");
        var withRecepcion = await Order();
        await Receive(h, s, "gr-r", recepcion);

        Assert.Equal((ProcurementErrors.LocationNotReceivable, ProcurementErrors.LocationNotReceivable), (intoCuring.Code, intoTransit.Code));
        Assert.Equal(JsonValueKind.Null, twoPatios.GetProperty("defaultLocationId").ValueKind); // two receivable locations and no RECEPCION
        Assert.Equal((recepcion, "RECEPCION"), (withRecepcion.GetProperty("defaultLocationId").GetGuid(), withRecepcion.GetProperty("defaultLocationCode").GetString()));
        Assert.Equal(2L, await h.CountAsync("pur.goods_receipt"));
    }

    [Fact]
    public async Task The_only_receivable_location_is_the_default()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var patio = await h.CreateLocationAsync(p.PlantId, "PATIO");
        await h.AdminRequireAsync(
            $"INSERT INTO md.location (location_id, company_id, plant_id, code, is_curing) VALUES (gen_random_uuid(), '{h.CompanyId}', '{p.PlantId}', 'CURADO', true)");
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "po", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 1m, 100m)]), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "po-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "po-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());

        var order = JsonDocument.Parse(await h.QueryAsync(new ListPurchaseOrdersToReceive(h.CompanyId, p.Controller, p.PlantId), new ListPurchaseOrdersToReceiveHandler()))
            .RootElement.GetProperty("items")[0];

        Assert.Equal((patio, "PATIO"), (order.GetProperty("defaultLocationId").GetGuid(), order.GetProperty("defaultLocationCode").GetString()));
    }

    [Fact]
    public async Task The_printed_total_is_kept_and_compared_with_the_determined_gross()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync(); // 6 t received at 1 500
        await h.EnableInvoicePostingAsync();
        RegisterSupplierInvoice Register(string key, string ncf, decimal? printed)
            => new(h.CompanyId, s.Clerk, key, s.Purchasing.SupplierId, ncf, Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1500m)], printed);

        var zero = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register("r0", "B0100000001", 0m), new RegisterSupplierInvoiceHandler()));
        var cents = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register("r1", "B0100000001", 10620.005m), new RegisterSupplierInvoiceHandler()));
        var ncf = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register("r2", "B01-0001", 10620.5m), new RegisterSupplierInvoiceHandler()));
        var si = (await h.RunAsync(Register("r3", "B0100000001", 10620.5m), new RegisterSupplierInvoiceHandler())).ResultRef;
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register("r4", "b0100000001", null), new RegisterSupplierInvoiceHandler()));
        async Task<JsonElement> Detail() => JsonDocument.Parse(await h.QueryAsync(new GetSupplierInvoice(h.CompanyId, s.Clerk, si), new GetSupplierInvoiceHandler())).RootElement;
        var registered = await Detail();
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var posted = await Detail();
        var listed = JsonDocument.Parse(await h.QueryAsync(new ListSupplierInvoices(h.CompanyId, s.Clerk), new ListSupplierInvoicesHandler())).RootElement.GetProperty("items")[0];

        Assert.Equal((ProcurementErrors.PrintedTotalInvalid, ProcurementErrors.PrintedTotalInvalid), (zero.Code, cents.Code));
        Assert.Equal((ProcurementErrors.FiscalNumberInvalid, ProcurementErrors.FiscalNumberUsed), (ncf.Code, duplicate.Code));
        Assert.Equal(("10620.50", JsonValueKind.Null), (registered.GetProperty("printedTotal").GetString(), registered.GetProperty("printedTotalDifference").ValueKind));

        // Gross = 9 000 net + 1 620 ITBIS = 10 620.00; printed 10 620.50 → +0.50.
        Assert.Equal((10620m, "0.50"), (decimal.Parse(posted.GetProperty("grossTotal").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            posted.GetProperty("printedTotalDifference").GetString()));
        Assert.Equal("0.50", listed.GetProperty("printedTotalDifference").GetString());
    }
}
