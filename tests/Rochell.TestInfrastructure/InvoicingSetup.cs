using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.TestInfrastructure;

/// <summary>An approved PO for sand (10 t at the given price) with a posted receipt, and an accounts-payable clerk.</summary>
public sealed record TestInvoicing(TestReceiving Receiving, Guid PurchaseOrderId, Guid PoLineId, Guid GoodsReceiptId, Guid GoodsReceiptLineId, Guid Clerk)
{
    public TestPurchasing Purchasing => Receiving.Purchasing;
}

public static class InvoicingSetup
{
    public static async Task<TestInvoicing> CreateInvoicingSetupAsync(this TestHarness h, decimal received = 6m, decimal price = 1500m)
    {
        ArgumentNullException.ThrowIfNull(h);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "inv-po-c", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", 10m, price)]), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "inv-po-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "inv-po-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        var poLine = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
        var gr = await h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, r.Storekeeper, "inv-gr", p.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-5), [new(poLine, received)]),
            new PostGoodsReceiptHandler());
        var grLine = JsonDocument.Parse(gr.ResultPayload).RootElement.GetProperty("lines")[0].GetProperty("grLineId").GetGuid();
        return new TestInvoicing(r, po, poLine, gr.ResultRef, grLine, await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR"));
    }
}
