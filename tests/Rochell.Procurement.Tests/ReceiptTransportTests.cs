using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// REC-01 (E-REC-1, 2, 4): a receipt names our ACTIVE truck and driver and the supplier's delivery note, which is received once per
/// supplier while the receipt stands; the report lists each line with its transport and totals per material, also as CSV.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReceiptTransportTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<(Guid Po, Guid Sand, Guid Cement)> OrderAsync(TestHarness h, TestReceiving r)
    {
        var p = r.Purchasing;
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "po", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 10m, 1500m), new(p.Cement, "t", 5m, 7800m)]),
            new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "po-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "po-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        return (po,
            await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p AND item_id = @i", ("p", po), ("i", p.Sand)),
            await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p AND item_id = @i", ("p", po), ("i", p.Cement)));
    }

    private static PostGoodsReceipt Receipt(TestHarness h, TestReceiving r, Guid po, string key, GoodsReceiptLineInput[] lines, Guid? vehicle, Guid? driver, string? note)
        => new(h.CompanyId, r.Storekeeper, key, r.Purchasing.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-5), lines, null, vehicle, driver, note);

    [Fact]
    public async Task A_receipt_names_our_truck_driver_and_the_suppliers_note_once_and_the_report_lists_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var (po, sand, cement) = await OrderAsync(h, r);
        await h.AdminRequireAsync($"UPDATE log.vehicle SET status = 'INACTIVE' WHERE company_id = '{h.CompanyId}'");
        var inactive = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Receipt(h, r, po, "g0", [new(sand, 4m)], r.Vehicle, r.Driver, "CP-100"), new PostGoodsReceiptHandler()));
        await h.AdminRequireAsync($"UPDATE log.vehicle SET status = 'ACTIVE' WHERE company_id = '{h.CompanyId}'");
        var noDriver = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Receipt(h, r, po, "g1", [new(sand, 4m)], r.Vehicle, null, "CP-100"), new PostGoodsReceiptHandler()));
        var noNote = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Receipt(h, r, po, "g2", [new(sand, 4m)], r.Vehicle, r.Driver, "  "), new PostGoodsReceiptHandler()));
        var first = await h.RunAsync(Receipt(h, r, po, "g3", [new(sand, 4m), new(cement, 2m)], r.Vehicle, r.Driver, "CP-100"), new PostGoodsReceiptHandler());
        var again = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Receipt(h, r, po, "g4", [new(sand, 3m)], r.Vehicle, r.Driver, "cp-100"), new PostGoodsReceiptHandler()));
        await h.RunAsync(Receipt(h, r, po, "g5", [new(sand, 3m)], r.Vehicle, r.Driver, "CP-101"), new PostGoodsReceiptHandler());

        Assert.Equal(
            (ProcurementErrors.ReceiptTransportRequired, ProcurementErrors.ReceiptTransportRequired, ProcurementErrors.SupplierDeliveryNoteRequired, ProcurementErrors.SupplierDeliveryNoteUsed),
            (inactive.Code, noDriver.Code, noNote.Code, again.Code));
        Assert.Equal("CP-100:Chofer de recepción", await h.ScalarAsync<string>(
            "SELECT gr.supplier_delivery_note || ':' || d.full_name FROM pur.goods_receipt gr JOIN log.driver d ON d.driver_id = gr.driver_id WHERE gr.gr_id = @g", ("g", first.ResultRef)));

        // E-REC-4: one row per line, totals per material and unit (sand 4 + 3 t in two receipts, cement 2 t in one).
        var report = JsonDocument.Parse(await h.QueryAsync(new GetGoodsReceiptReport(h.CompanyId, r.Storekeeper, Today(h), Today(h)), new GetGoodsReceiptReportHandler())).RootElement;
        Assert.Equal(3, report.GetProperty("rows").GetArrayLength());
        Assert.Equal("CEMENTO-GRIS:t:2:1|ARENA-LAVADA:t:7:2", string.Join('|', report.GetProperty("totals").EnumerateArray()
            .OrderByDescending(t => t.GetProperty("itemCode").GetString(), StringComparer.Ordinal)
            .Select(t => $"{t.GetProperty("itemCode").GetString()}:{t.GetProperty("uom").GetString()}:{decimal.Parse(t.GetProperty("quantity").GetString()!, System.Globalization.CultureInfo.InvariantCulture):0.##}:{t.GetProperty("receipts").GetInt32()}")));
        var csv = GoodsReceiptCsv.Report(await h.QueryAsync(new GetGoodsReceiptReport(h.CompanyId, r.Storekeeper, Today(h), Today(h), ItemId: r.Purchasing.Cement), new GetGoodsReceiptReportHandler()));
        Assert.StartsWith("Fecha,Recepción,Orden,Proveedor,Conduce del proveedor,Camión,Chofer,Material", csv, StringComparison.Ordinal);
        Assert.Contains(",CP-100,", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("CP-101", csv, StringComparison.Ordinal);
    }
}
