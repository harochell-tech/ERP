using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// Baseline §17 PR-18: AT-01 and AT-02 end to end through the API — each actor signs in through OIDC and works over HTTP.
/// Deployment configuration (plant, locations, accounts and maps, periods, policies, posting rules, fiscal rules) comes from the
/// fixtures, as the deployment tools and the configuration commands put it in a real environment.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AcceptanceTests(PostgresFixture postgres)
{
    private const string Withholding = """{"tax_code":"RET_ITBIS","rate":"0.30","base":"ITBIS","party_types":["COMPANY"]}""";

    private static string Role(string role) => $"(SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = '{role}')";

    [Trait("Acceptance", "INT-01")]
    [Trait("Acceptance", "AT-01")]
    [Fact]
    public async Task AT01_two_receipts_of_20_t_against_an_approved_order_of_40_t_at_1000()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var r = await h.CreateReceivingSetupAsync();
        var actors = await Actors.SignInAsync(api, r);

        var (po, poLine) = await ApprovedOrderAsync(h, r, actors);
        var receipts = await ReceiveAsync(h, r, actors, po, poLine);

        // PO RECEIVED, read back through the API.
        var order = await actors.Buyer.GetOkAsync($"/api/v1/companies/{h.CompanyId}/procurement/purchase-orders/{po}");
        Assert.Equal("RECEIVED", order.GetProperty("status").GetString());
        Assert.Equal("40.000000", order.GetProperty("lines")[0].GetProperty("qtyReceived").GetString());
        Assert.Equal(2, order.GetProperty("goodsReceipts").GetArrayLength());
        Assert.Equal("DRAFT>PENDING_APPROVAL>APPROVED>PARTIALLY_RECEIVED>RECEIVED", string.Join('>', order.GetProperty("history").EnumerateArray().Select(c => c.GetProperty("to").GetString())));
        var list = await actors.Storekeeper.GetOkAsync($"/api/v1/companies/{h.CompanyId}/procurement/goods-receipts?purchaseOrderId={po}");
        Assert.All(list.GetProperty("items").EnumerateArray(), gr => Assert.Equal("POSTED|POSTED", gr.GetProperty("documentStatus").GetString() + "|" + gr.GetProperty("accountingStatus").GetString()));

        // Journals R-01 × 2 (Dr RAW 20 000 / Cr GRNI 20 000 each), read and explained through the API by the Controller.
        foreach (var gr in receipts)
        {
            var detail = await actors.Storekeeper.GetOkAsync($"/api/v1/companies/{h.CompanyId}/procurement/goods-receipts/{gr}");
            var journals = await actors.Controller.GetOkAsync($"/api/v1/companies/{h.CompanyId}/finance/events/{detail.GetProperty("postingEventId").GetGuid()}/journals");
            var journal = Assert.Single(journals.GetProperty("journals").EnumerateArray());
            Assert.Equal("R-01", journal.GetProperty("ruleCode").GetString());
            Assert.Equal(
                "RAW_MATERIAL:20000.0000:0.0000,GRNI:0.0000:20000.0000",
                string.Join(',', journal.GetProperty("entries").EnumerateArray().Select(e => $"{e.GetProperty("accountRole").GetString()}:{e.GetProperty("debit").GetString()}:{e.GetProperty("credit").GetString()}")));
            var raw = journal.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("accountRole").GetString() == "RAW_MATERIAL");
            var explanation = await actors.Controller.GetOkAsync($"/api/v1/companies/{h.CompanyId}/finance/entries/{raw.GetProperty("glEntryId").GetGuid()}/explanation");
            Assert.Equal("R-01", explanation.GetProperty("rule").GetProperty("code").GetString());
        }

        // Stock 40 t, value 40 000, GRNI 40 000 Cr; each receipt's value entry ↔ GL line 1:1 (P-1); ledgers PENDING_SEAL (sealer off).
        Assert.Equal("40.000000|40.000000/40000.0000|-40000.0000|40000.0000", await h.ScalarAsync<string>(
            $"SELECT (SELECT sum(quantity) FROM inv.inv_stock_balance) || '|' || (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' || {Role("GRNI")} || '|' || {Role("RAW_MATERIAL")}"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code = 'R-01'"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM inv.inv_value_entry v JOIN fin.gl_entry e ON e.inv_value_entry_id = v.value_entry_id"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM audit.integrity_state WHERE integrity_status <> 'PENDING_SEAL'"));
    }

    [Trait("Acceptance", "AT-02")]
    [Fact]
    public async Task AT02_invoice_of_40_t_at_1000_with_ITBIS_and_withholding_clears_GRNI_and_AP_GL_matches()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync(withholdingDefinition: Withholding);
        var actors = await Actors.SignInAsync(api, r);
        var clerk = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR"));
        var (po, poLine) = await ApprovedOrderAsync(h, r, actors);
        await ReceiveAsync(h, r, actors, po, poLine);
        var today = BusinessCalendar.DefaultBusinessDate(DateTime.UtcNow);

        var si = (await clerk.OkAsync(h.CompanyId, "procurement", "register-supplier-invoice", new
        {
            partyId = r.Purchasing.SupplierId,
            supplierFiscalNumber = "B0100000001",
            docDate = today,
            dueDate = today.AddDays(30),
            lines = new[] { new { purchaseOrderLineId = poLine, quantity = "40", unitPrice = "1000" } },
        })).GetProperty("resultRef").GetGuid();
        var match = await clerk.OkAsync(h.CompanyId, "procurement", "match-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 1 });
        Assert.Equal("MATCHED", match.GetProperty("result").GetProperty("status").GetString());
        await clerk.OkAsync(h.CompanyId, "procurement", "post-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 2 });

        // T = 18 % × 40 000 = 7 200; W = 30 % × 7 200 = 2 160; AP = 40 000 + 7 200 − 2 160 = 45 040.
        var invoice = await clerk.GetOkAsync($"/api/v1/companies/{h.CompanyId}/procurement/supplier-invoices/{si}");
        Assert.Equal("MATCHED|POSTED", invoice.GetProperty("documentStatus").GetString() + "|" + invoice.GetProperty("accountingStatus").GetString());
        Assert.Equal("45040.0000/45040.0000", invoice.GetProperty("apDocument").GetProperty("originalAmount").GetString() + "/" + invoice.GetProperty("apDocument").GetProperty("openAmount").GetString());
        Assert.Equal(
            "ITBIS:7200.0000,RET_ITBIS:2160.0000",
            string.Join(',', invoice.GetProperty("taxes").EnumerateArray().Select(t => t.GetProperty("taxCode").GetString() + ":" + t.GetProperty("amount").GetString()).Order(StringComparer.Ordinal)));
        Assert.True(invoice.GetProperty("lines")[0].GetProperty("match").GetProperty("withinTolerance").GetBoolean());
        Assert.Equal("0.0000|-45040.0000|7200.0000|-2160.0000|40000.0000", await h.ScalarAsync<string>(
            $"SELECT {Role("GRNI")} || '|' || {Role("AP_CONTROL")} || '|' || {Role("ITBIS_RECOVERABLE")} || '|' || {Role("WITHHOLDING_PAYABLE")} || '|' || {Role("RAW_MATERIAL")}"));

        // D = 0 (Patch 1): only R-04 for the invoice, no price-difference value entries.
        var journals = await actors.Controller.GetOkAsync($"/api/v1/companies/{h.CompanyId}/finance/events/{invoice.GetProperty("postingEventId").GetGuid()}/journals");
        Assert.Equal("R-04", Assert.Single(journals.GetProperty("journals").EnumerateArray()).GetProperty("ruleCode").GetString());
        Assert.Equal(2L, await h.CountAsync("inv.inv_value_entry"));

        // AP-GL MATCHED: run by the Controller and read back through the API.
        var run = await actors.Controller.OkAsync(h.CompanyId, "reconciliation", "run-reconciliation", new { reconCodes = new[] { "AP-GL" } });
        var runId = Assert.Single(run.GetProperty("result").GetProperty("runs").EnumerateArray()).GetProperty("runId").GetGuid();
        var detail = await actors.Controller.GetOkAsync($"/api/v1/companies/{h.CompanyId}/reconciliation/runs/{runId}");
        Assert.Equal("AP-GL|MATCHED|0", $"{detail.GetProperty("run").GetProperty("reconCode").GetString()}|{detail.GetProperty("run").GetProperty("status").GetString()}|{detail.GetProperty("exceptions").GetArrayLength()}");
    }

    private static async Task<(Guid Po, Guid PoLine)> ApprovedOrderAsync(TestHarness h, TestReceiving r, Actors actors)
    {
        var p = r.Purchasing;
        var po = (await actors.Buyer.OkAsync(h.CompanyId, "procurement", "create-purchase-order", new
        {
            plantId = p.PlantId,
            partyId = p.SupplierId,
            orderDate = BusinessCalendar.DefaultBusinessDate(DateTime.UtcNow),
            lines = new[] { new { itemId = p.Sand, uom = "t", quantity = "40", unitPrice = "1000.00" } },
        })).GetProperty("resultRef").GetGuid();
        await actors.Buyer.OkAsync(h.CompanyId, "procurement", "submit-purchase-order", new { plantId = p.PlantId, purchaseOrderId = po, expectedVersion = 1 });
        await actors.Approver.OkAsync(h.CompanyId, "procurement", "approve-purchase-order", new { plantId = p.PlantId, purchaseOrderId = po, expectedVersion = 2 });
        var order = await actors.Approver.GetOkAsync($"/api/v1/companies/{h.CompanyId}/procurement/purchase-orders/{po}");
        Assert.Equal("APPROVED", order.GetProperty("status").GetString());
        return (po, order.GetProperty("lines")[0].GetProperty("poLineId").GetGuid());
    }

    private static async Task<Guid[]> ReceiveAsync(TestHarness h, TestReceiving r, Actors actors, Guid po, Guid poLine)
    {
        var receipts = new Guid[2];
        for (var i = 0; i < receipts.Length; i++)
        {
            var response = await actors.Storekeeper.OkAsync(h.CompanyId, "procurement", "post-goods-receipt", new
            {
                plantId = r.Purchasing.PlantId,
                purchaseOrderId = po,
                locationId = r.LocationA,
                occurredAt = DateTime.UtcNow.AddMinutes(-10 + i),
                lines = new[] { new { purchaseOrderLineId = poLine, quantity = "20" } },
                weighTicketRef = $"TK-{i + 1}",
            });
            receipts[i] = response.GetProperty("resultRef").GetGuid();
        }

        return receipts;
    }

    private sealed record Actors(HttpClient Buyer, HttpClient Approver, HttpClient Storekeeper, HttpClient Controller)
    {
        public static async Task<Actors> SignInAsync(ApiHost api, TestReceiving r)
            => new(
                await api.SignInAsSessionUserAsync(r.Purchasing.Buyer),
                await api.SignInAsSessionUserAsync(r.Purchasing.Approver),
                await api.SignInAsSessionUserAsync(r.Storekeeper),
                await api.SignInAsSessionUserAsync(r.Purchasing.Controller));
    }
}
