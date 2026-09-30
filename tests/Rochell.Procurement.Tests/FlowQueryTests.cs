using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Explain;
using Rochell.Platform.Commands;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>UX3-01: orders to receive with open and receivable quantities (E-UX3-5) and Explain with names (E-UX3-4).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FlowQueryTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> ToReceiveAsync(TestHarness h, TestInvoicing s)
        => JsonDocument.Parse(await h.QueryAsync(
            new ListPurchaseOrdersToReceive(h.CompanyId, s.Receiving.Storekeeper, s.Purchasing.PlantId), new ListPurchaseOrdersToReceiveHandler())).RootElement;

    private static Task<CommandResult> Receive(TestHarness h, TestInvoicing s, string key, decimal quantity)
        => h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, s.Receiving.Storekeeper, key, s.Purchasing.PlantId, s.PurchaseOrderId, s.Receiving.LocationA, h.Clock.UtcNow.AddMinutes(-1), [new(s.PoLineId, quantity)]),
            new PostGoodsReceiptHandler());

    [Fact]
    public async Task Orders_to_receive_show_the_open_quantity_and_the_most_a_receipt_may_take()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync(); // 10 t ordered, 6 t received, receipt tolerance 2 % (PolicySetup.Purchasing)

        var partial = await ToReceiveAsync(h, s);
        var over = await Assert.ThrowsAsync<DomainException>(() => Receive(h, s, "over", 4.200001m));
        await Receive(h, s, "rest", 4.2m); // exactly the receivable maximum
        var received = await ToReceiveAsync(h, s);
        var detail = JsonDocument.Parse(await h.QueryAsync(
            new GetPurchaseOrder(h.CompanyId, s.Receiving.Storekeeper, s.PurchaseOrderId, s.Purchasing.PlantId), new GetPurchaseOrderHandler())).RootElement;

        // Open = 10 − 6 = 4; receivable = 10 × (1 + 0.02) + 0 approved − 6 = 4.2.
        var order = partial.GetProperty("items").EnumerateArray().Single(o => o.GetProperty("purchaseOrderId").GetGuid() == s.PurchaseOrderId);
        var line = order.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal(("PARTIALLY_RECEIVED", "Agregados del Este, S.R.L."), (order.GetProperty("status").GetString(), order.GetProperty("supplierName").GetString()));
        Assert.Equal(("10.000000", "6.000000", "4.000000", "4.200000"), (line.GetProperty("qtyOrdered").GetString(), line.GetProperty("qtyReceived").GetString(),
            line.GetProperty("openQuantity").GetString(), line.GetProperty("maxReceivable").GetString()));
        Assert.Equal(("ARENA-LAVADA", "t"), (line.GetProperty("itemCode").GetString(), line.GetProperty("uom").GetString()));
        Assert.Equal(ProcurementErrors.ReceiptToleranceExceeded, over.Code);

        // 10.2 received ≥ 10 ordered: the order is RECEIVED and leaves the list; nothing is open any more.
        Assert.DoesNotContain(received.GetProperty("items").EnumerateArray(), o => o.GetProperty("purchaseOrderId").GetGuid() == s.PurchaseOrderId);
        Assert.Equal("RECEIVED", detail.GetProperty("status").GetString());
        Assert.Equal(0m, decimal.Parse(detail.GetProperty("lines")[0].GetProperty("openQuantity").GetString()!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_approved_over_receipt_raises_what_a_receipt_may_take()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.AdminRequireAsync(
            $"BEGIN; SET LOCAL session_replication_role = replica; UPDATE pur.purchase_order_line SET qty_over_receipt_approved = 1.5 WHERE po_line_id = '{s.PoLineId}'; COMMIT;");

        var list = await ToReceiveAsync(h, s);

        // 10 × 1.02 + 1.5 approved over-receipt − 6 = 5.7.
        var line = list.GetProperty("items").EnumerateArray().Single(o => o.GetProperty("purchaseOrderId").GetGuid() == s.PurchaseOrderId).GetProperty("lines")[0];
        Assert.Equal(("4.000000", "5.700000"), (line.GetProperty("openQuantity").GetString(), line.GetProperty("maxReceivable").GetString()));
    }

    [Fact]
    public async Task Explain_returns_plant_item_and_party_names_beside_their_ids()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.AdminRequireAsync($"UPDATE md.plant SET name = 'Planta Higüey' WHERE plant_id = '{s.Purchasing.PlantId}'");
        var plantCode = await h.ScalarAsync<string>("SELECT code FROM md.plant WHERE plant_id = @p", ("p", s.Purchasing.PlantId));

        async Task<JsonElement> Entry(string ruleLineCode)
        {
            var id = await h.ScalarAsync<Guid>("SELECT gl_entry_id FROM fin.gl_entry WHERE rule_line_code = @l", ("l", ruleLineCode));
            return JsonDocument.Parse(await h.QueryAsync(new ExplainEntry(h.CompanyId, s.Purchasing.Controller, id), new ExplainEntryHandler())).RootElement.GetProperty("entry");
        }

        var inventory = await Entry("R01-DR-INV");
        var grni = await Entry("R01-CR-GRNI");

        Assert.Equal((plantCode, "Planta Higüey", "ARENA-LAVADA", "ARENA-LAVADA"), (inventory.GetProperty("plantCode").GetString(), inventory.GetProperty("plantName").GetString(),
            inventory.GetProperty("itemCode").GetString(), inventory.GetProperty("itemDescription").GetString()));
        Assert.Equal(s.Purchasing.Sand, inventory.GetProperty("itemId").GetGuid());
        Assert.Equal(("Agregados del Este, S.R.L.", s.Purchasing.SupplierId), (grni.GetProperty("partyLegalName").GetString(), grni.GetProperty("partyId").GetGuid()));
    }
}
