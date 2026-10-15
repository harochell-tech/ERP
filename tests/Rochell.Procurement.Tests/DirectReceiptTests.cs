using System.Globalization;
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
/// REC-02 (E-REC-6…8, E-REC-02-1…7): a receipt without a purchase order creates an order marked DIRECT_RECEIPT. Within the price
/// policy the service identity approves it and the receipt posts at once; beyond it, or without a reference or a policy value, the
/// order waits and nothing enters inventory until another person approves it; rejecting it cancels the order and frees the note.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DirectReceiptTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static Task SetDeviationAsync(TestHarness h, string fraction)
        => h.AdminRequireAsync(
            $"""
            DO $$ BEGIN
              PERFORM set_config('session_replication_role', 'replica', true);
              INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value)
              SELECT company_id, policy_version_id, 'direct_receipt_price_deviation_pct', to_jsonb('{fraction}'::text)
              FROM acc.accounting_policy_version WHERE company_id = '{h.CompanyId}' AND policy_code = 'PURCHASING' AND status = 'ACTIVE';
            END $$
            """);

    /// <summary>An approved order of sand at 1,500.00 / t from the fixture's supplier: the last price of sand in tonnes.</summary>
    private static async Task<string> PriorOrderAsync(TestHarness h, TestReceiving r)
    {
        var p = r.Purchasing;
        var created = await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "prior", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 10m, 1500m)]), new CreatePurchaseOrderHandler());
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "prior-s", p.PlantId, created.ResultRef, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "prior-a", p.PlantId, created.ResultRef, 2), new ApprovePurchaseOrderHandler());
        return (await h.ScalarAsync<string>("SELECT po_no FROM pur.purchase_order WHERE po_id = @p", ("p", created.ResultRef)))!;
    }

    private static PostDirectGoodsReceipt Direct(TestHarness h, TestReceiving r, string key, DirectReceiptLineInput[] lines, string note, DateTime? occurredAt = null)
        => new(h.CompanyId, r.Storekeeper, key, r.Purchasing.PlantId, r.Purchasing.SupplierId, r.LocationA, occurredAt ?? h.Clock.UtcNow.AddMinutes(-30), lines, r.Vehicle, r.Driver, note);

    private static async Task<JsonElement> RunDirectAsync(TestHarness h, PostDirectGoodsReceipt command)
    {
        var outcome = await h.RunAsync(command, new PostDirectGoodsReceiptHandler());
        return JsonDocument.Parse(outcome.ResultPayload).RootElement;
    }

    [Fact]
    public async Task Within_the_policy_the_service_identity_approves_and_the_receipt_posts_at_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await SetDeviationAsync(h, "0.10");
        var priorNo = await PriorOrderAsync(h, r);

        // 1,600.00 against 1,500.00: 6.67 % ≤ 10 %.
        var result = await RunDirectAsync(h, Direct(h, r, "d1", [new(r.Purchasing.Sand, "t", 6m, 1600m)], "CP-500"));
        var poId = result.GetProperty("purchaseOrderId").GetGuid();
        var check = result.GetProperty("priceCheck")[0];

        Assert.Equal(("POSTED", false), (result.GetProperty("status").GetString(), result.GetProperty("requiresApproval").GetBoolean()));
        Assert.Equal((DirectReceiptPriceSource.LastPurchase, priorNo, "0.0667", true),
            (check.GetProperty("source").GetString(), check.GetProperty("sourcePoNo").GetString(), check.GetProperty("deviationPct").GetString(), check.GetProperty("withinPolicy").GetBoolean()));
        Assert.Equal("DIRECT_RECEIPT:RECEIVED:true:true", await h.ScalarAsync<string>(
            $"SELECT origin || ':' || status || ':' || (approved_by = '{DirectReceipts.ServiceUserId}') || ':' || (created_by <> approved_by) FROM pur.purchase_order WHERE po_id = @p", ("p", poId)));
        Assert.Equal("CP-500:6.000000:1600.000000:true", await h.ScalarAsync<string>(
            """
            SELECT gr.supplier_delivery_note || ':' || grl.qty || ':' || grl.unit_price || ':' || (d.gr_id = gr.gr_id AND d.closed_at IS NOT NULL)
            FROM pur.goods_receipt gr JOIN pur.goods_receipt_line grl ON grl.gr_id = gr.gr_id JOIN pur.direct_receipt d ON d.po_id = gr.po_id
            WHERE gr.po_id = @p
            """,
            ("p", poId)));

        // E-REC-02-7: the order shows its mark and what was received.
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetPurchaseOrder(h.CompanyId, r.Storekeeper, poId), new GetPurchaseOrderHandler())).RootElement;
        Assert.Equal(("DIRECT_RECEIPT", "CP-500", "Chofer de recepción"),
            (detail.GetProperty("origin").GetString(), detail.GetProperty("directReceipt").GetProperty("supplierDeliveryNote").GetString(), detail.GetProperty("directReceipt").GetProperty("driverName").GetString()));
    }

    [Fact]
    public async Task Beyond_the_policy_or_without_a_reference_it_waits_and_posts_when_another_person_approves()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await SetDeviationAsync(h, "0.10");
        await PriorOrderAsync(h, r);
        var arrival = Precision.ToMicroseconds(h.Clock.UtcNow.AddHours(-2));

        // Sand at 1,700.00 is 13.33 % above 1,500.00; cement was never bought and has no standard price.
        var result = await RunDirectAsync(h, Direct(h, r, "d1", [new(r.Purchasing.Sand, "t", 4m, 1700m), new(r.Purchasing.Cement, "t", 2m, 7800m)], "CP-600", arrival));
        var poId = result.GetProperty("purchaseOrderId").GetGuid();
        var checks = result.GetProperty("priceCheck");

        Assert.Equal(("PENDING_APPROVAL", true), (result.GetProperty("status").GetString(), result.GetProperty("requiresApproval").GetBoolean()));
        Assert.Equal(("0.1333", false, JsonValueKind.Null, false),
            (checks[0].GetProperty("deviationPct").GetString(), checks[0].GetProperty("withinPolicy").GetBoolean(), checks[1].GetProperty("referencePrice").ValueKind, checks[1].GetProperty("withinPolicy").GetBoolean()));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM pur.goods_receipt WHERE po_id = @p", ("p", poId)));

        // The note is held while it waits.
        var held = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Direct(h, r, "d2", [new(r.Purchasing.Sand, "t", 1m, 1500m)], "cp-600"), new PostDirectGoodsReceiptHandler()));
        Assert.Equal(ProcurementErrors.SupplierDeliveryNoteUsed, held.Code);

        // E-REC-02-4: the purchasing approver approves; the receipt posts with the arrival time it was entered with.
        var approve = await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, r.Purchasing.Approver, "approve", r.Purchasing.PlantId, poId, 2), new ApprovePurchaseOrderHandler());
        Assert.Equal("RECEIVED", JsonDocument.Parse(approve.ResultPayload).RootElement.GetProperty("status").GetString());
        Assert.Equal("RECEIVED:2:true:true", await h.ScalarAsync<string>(
            """
            SELECT po.status || ':' || (SELECT count(*) FROM pur.goods_receipt_line grl JOIN pur.goods_receipt gr ON gr.gr_id = grl.gr_id WHERE gr.po_id = po.po_id)
                   || ':' || (SELECT gr.occurred_at = @arrival FROM pur.goods_receipt gr WHERE gr.po_id = po.po_id)
                   || ':' || (SELECT d.closed_at IS NOT NULL AND d.gr_id IS NOT NULL FROM pur.direct_receipt d WHERE d.po_id = po.po_id)
            FROM pur.purchase_order po WHERE po.po_id = @p
            """,
            ("p", poId),
            ("arrival", arrival)));
    }

    [Fact]
    public async Task Rejecting_it_cancels_the_order_frees_the_note_and_without_a_policy_value_everything_waits()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await PriorOrderAsync(h, r);

        // No direct_receipt_price_deviation_pct in the policy: even the last price itself waits (E-REC-02-3).
        var first = await RunDirectAsync(h, Direct(h, r, "d1", [new(r.Purchasing.Sand, "t", 3m, 1500m)], "CP-700"));
        Assert.Equal(("PENDING_APPROVAL", JsonValueKind.Null), (first.GetProperty("status").GetString(), first.GetProperty("priceCheck")[0].GetProperty("allowedPct").ValueKind));
        var poId = first.GetProperty("purchaseOrderId").GetGuid();

        await h.RunAsync(new RejectPurchaseOrder(h.CompanyId, r.Purchasing.Approver, "reject", r.Purchasing.PlantId, poId, 2, "Precio no acordado"), new RejectPurchaseOrderHandler());
        Assert.Equal("CANCELLED:true:0", await h.ScalarAsync<string>(
            "SELECT po.status || ':' || (SELECT d.closed_at IS NOT NULL AND d.gr_id IS NULL FROM pur.direct_receipt d WHERE d.po_id = po.po_id) || ':' || (SELECT count(*) FROM pur.goods_receipt gr WHERE gr.po_id = po.po_id) FROM pur.purchase_order po WHERE po.po_id = @p",
            ("p", poId)));

        // The note is free again; the reference shown to the screen is the last price with no allowed deviation.
        var again = await RunDirectAsync(h, Direct(h, r, "d2", [new(r.Purchasing.Sand, "t", 3m, 1500m)], "CP-700"));
        Assert.Equal("PENDING_APPROVAL", again.GetProperty("status").GetString());
        var reference = JsonDocument.Parse(await h.QueryAsync(
            new GetDirectReceiptReference(h.CompanyId, r.Storekeeper, r.Purchasing.SupplierId, r.Purchasing.Sand, "t"), new GetDirectReceiptReferenceHandler())).RootElement;
        Assert.Equal(("1500", DirectReceiptPriceSource.LastPurchase, JsonValueKind.Null),
            (decimal.Parse(reference.GetProperty("referencePrice").GetString()!, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture),
             reference.GetProperty("source").GetString(), reference.GetProperty("allowedDeviationPct").ValueKind));

        // Without our truck or the supplier's note it is refused before anything is written.
        var noNote = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Direct(h, r, "d3", [new(r.Purchasing.Sand, "t", 1m, 1500m)], " "), new PostDirectGoodsReceiptHandler()));
        Assert.Equal(ProcurementErrors.SupplierDeliveryNoteRequired, noNote.Code);
    }
}
