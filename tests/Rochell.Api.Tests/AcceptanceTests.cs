using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Audit;
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

    /// <summary>
    /// VS#2 E2E-01 through the API (E-VS2-07-6): the AT-02 invoice (AP 45,040), then the proposal, a payment prepared by the treasurer and
    /// released by the Controller (R-09), the bank statement imported, the suggested match confirmed, the bank charge recognized
    /// (R-10), BANK-GL at zero and BANK-REC of the month closed after it ends. The supplier's account verified 73 h ago, the bank's GL
    /// account, the BANK_CHARGES map and the ledger seal come from the fixtures, as deployment and time put them in a real environment.
    /// </summary>
    [Trait("AcceptanceVs2", "E2E-01")]
    [Fact]
    public async Task E2E01_invoice_payment_statement_match_charge_and_BANK_REC_close_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync(withholdingDefinition: Withholding);
        var actors = await Actors.SignInAsync(api, r);
        var clerk = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR"));
        var treasurerSession = await h.SessionWithRolesAsync("TESORERO");
        var treasurer = await api.SignInAsSessionUserAsync(treasurerSession);
        var (po, poLine) = await ApprovedOrderAsync(h, r, actors);
        await ReceiveAsync(h, r, actors, po, poLine);
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);
        var c = h.CompanyId;
        var si = (await clerk.OkAsync(c, "procurement", "register-supplier-invoice", new
        {
            partyId = r.Purchasing.SupplierId,
            supplierFiscalNumber = "B0100000001",
            docDate = today,
            dueDate = today.AddDays(30),
            lines = new[] { new { purchaseOrderLineId = poLine, quantity = "40", unitPrice = "1000" } },
        })).GetProperty("resultRef").GetGuid();
        await clerk.OkAsync(c, "procurement", "match-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 1 });
        await clerk.OkAsync(c, "procurement", "post-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 2 });

        // Treasury configuration: the bank's own GL account, the charges map, R-09 / R-10 approved and the bank account registered.
        await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
        await h.CreateActiveMapAsync("BANK_CHARGES", await h.CreateAccountAsync("6105", "Cargos bancarios", isControl: false));
        await actors.Controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = "R-09", version = 1 });
        await actors.Controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = "R-10", version = 1 });
        var bank = (await actors.Controller.OkAsync(c, "treasury", "register-bank-account", new { bankCode = "TEST_BANK", accountNumber = "0123456789", glAccountCode = "1101" }))
            .GetProperty("resultRef").GetGuid();
        await h.VerifiedPartyBankAccountAsync(r.Purchasing.SupplierId, treasurerSession, r.Purchasing.Controller, 1, "9876543210", 73);

        // Proposal → prepare (the total comes back from the server, E-UI-3) → release (R-09).
        var proposal = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/payment-proposal?dueUntil={today.AddDays(30):yyyy-MM-dd}");
        var supplier = Assert.Single(proposal.GetProperty("suppliers").EnumerateArray());
        Assert.Equal("PAYABLE|••••3210", $"{supplier.GetProperty("payability").GetString()}|{supplier.GetProperty("accountNumber").GetString()}");
        var invoice = Assert.Single(supplier.GetProperty("invoices").EnumerateArray());
        var prepared = await treasurer.OkAsync(c, "treasury", "prepare-supplier-payment", new
        {
            partyId = r.Purchasing.SupplierId,
            bankAccountId = bank,
            partyBankAccountId = supplier.GetProperty("partyBankAccountId").GetGuid(),
            valueDate = today,
            bankReference = "TRF-889",
            applications = new[] { new { apDocId = invoice.GetProperty("apDocId").GetGuid(), amount = "45040.00" } },
        });
        var payment = prepared.GetProperty("resultRef").GetGuid();
        Assert.Equal("PAG-000001|45040.00", $"{prepared.GetProperty("result").GetProperty("paymentNo").GetString()}|{prepared.GetProperty("result").GetProperty("amount").GetString()}");
        await actors.Controller.OkAsync(c, "treasury", "release-supplier-payment", new { paymentId = payment, expectedVersion = 1 });

        // Statement: the transfer and a 150.00 charge; opening 0.00 like the books, closing −45,190.00.
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{today:dd/MM/yyyy},TRF-889,Transferencia PAG-000001,45040.00,\n{today:dd/MM/yyyy},,Comision transferencia,150.00,\n";
        var statement = (await treasurer.OkAsync(c, "treasury", "import-bank-statement", new
        {
            bankAccountId = bank,
            fileName = "extracto.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
            periodFrom = today,
            periodTo = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1), // it covers the month's end, the close's cutoff
            openingBalance = "0.00",
            closingBalance = "-45190.00",
        })).GetProperty("resultRef").GetGuid();
        var suggestions = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-statements/{statement}/match-suggestions");
        var suggested = suggestions.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("candidates").GetArrayLength() == 1);
        Assert.Equal("PAYMENT_NO", suggested.GetProperty("candidates")[0].GetProperty("basis").GetString());
        await treasurer.OkAsync(c, "treasury", "match-bank-line", new
        {
            lineId = suggested.GetProperty("lineId").GetGuid(),
            expectedLineVersion = 1,
            paymentId = payment,
            expectedPaymentVersion = 2,
        });
        var unmatched = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-statement-lines?statementId={statement}&status=UNMATCHED");
        var charge = Assert.Single(unmatched.GetProperty("items").EnumerateArray()).GetProperty("lineId").GetGuid();
        await actors.Controller.OkAsync(c, "treasury", "recognize-bank-charge", new { lineId = charge, expectedVersion = 1 });

        var detail = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/payments/{payment}");
        Assert.Equal("CLEARED|PREPARED>RELEASED>CLEARED", $"{detail.GetProperty("status").GetString()}|{string.Join('>', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString()))}");
        var bankGl = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-accounts/{bank}/reconciliation?asOf={today:yyyy-MM-dd}");
        Assert.Equal(-45190m, decimal.Parse(bankGl.GetProperty("glBalance").GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal(0m, decimal.Parse(bankGl.GetProperty("difference").GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal(0, bankGl.GetProperty("findings").GetArrayLength());
        Assert.Equal("0.0000|0.0000", await h.ScalarAsync<string>($"SELECT {Role("AP_CONTROL")} || '|' || {Role("GRNI")}"));

        // After the month ends: seal, and the Controller closes BANK-REC of the month (BANK-GL, PAY-APPL, ACC-EVIDENCE clean).
        clock.Advance(TimeSpan.FromDays(40));
        var controller = await api.SignInAsSessionUserAsync(r.Purchasing.Controller);
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var periods = await controller.GetOkAsync($"/api/v1/companies/{c}/reconciliation/periods?year={today.Year}");
        var period = periods.GetProperty("items").EnumerateArray()
            .Single(p => DateOnly.Parse(p.GetProperty("startsOn").GetString()!, CultureInfo.InvariantCulture) <= today && today <= DateOnly.Parse(p.GetProperty("endsOn").GetString()!, CultureInfo.InvariantCulture))
            .GetProperty("periodId").GetGuid();
        await controller.OkAsync(c, "reconciliation", "close-component", new { periodId = period, component = "BANK-REC" });
        Assert.Equal("CLOSED", await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'BANK-REC'"));
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
