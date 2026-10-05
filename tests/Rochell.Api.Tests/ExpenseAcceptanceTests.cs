using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// GAS-1 E2E-G1 through the API (E-GAS-07-8): the Contador prepares the category «Teléfono» and the Controller approves it → Cuentas
/// por pagar registers the telephone bill without an order (30,000.00, type Telecomunicaciones: ITBIS 5,400.00, ISC 3,000.00, CDT
/// 600.00; 39,000.00 is over the 25,000.00 approval amount) → the Controller approves it → it is posted with P-37 → Tesorería pays it
/// and the bank statement matches the transfer (GAS-15, AP-GL clean) → the month's 606 carries it in its columns and TAX-606 agrees.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExpenseAcceptanceTests(PostgresFixture postgres)
{
    private const string Telecom =
        """{"label":"Telecomunicaciones","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"},{"tax_code":"ISC","rate":"0.10","effect":"SELECTIVE_TAX"},{"tax_code":"CDT","rate":"0.02","effect":"OTHER_TAX"}]}""";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    private static string Role(string role) => $"(SELECT coalesce(sum(debit - credit), 0)::numeric(19,2) FROM fin.gl_entry WHERE account_role = '{role}')";

    [Trait("AcceptanceGas1", "E2E-G1")]
    [Trait("AcceptanceGas1", "GAS-15")]
    [Fact]
    public async Task E2EG1_category_telephone_bill_approved_posted_paid_matched_and_in_the_606_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

        // Deployment: the Telecomunicaciones type in force, the three tax expense accounts mapped (A-01).
        await h.ActivateRuleAsync(await h.FiscalActorsAsync(initEnvironment: false), "telecom", "TELECOM", FiscalRuleKinds.PurchaseTaxType, Telecom, new DateOnly(2026, 1, 1));
        await h.CreateActiveMapAsync("SELECTIVE_TAX_EXPENSE", await h.CreateAccountAsync("63950", "Impuesto selectivo al consumo", isControl: false));
        await h.CreateActiveMapAsync("OTHER_TAX_EXPENSE", await h.CreateAccountAsync("63960", "Otros impuestos y tasas", isControl: false));
        await h.CreateActiveMapAsync("LEGAL_TIP_EXPENSE", await h.CreateAccountAsync("63900", "Propinas", isControl: false));
        await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);

        var controllerSession = r.Purchasing.Controller;
        var controller = await api.SignInAsSessionUserAsync(controllerSession);
        var contador = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTADOR"));
        var clerk = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR"));
        var treasurerSession = await h.SessionWithRolesAsync("TESORERO");
        var treasurer = await api.SignInAsSessionUserAsync(treasurerSession);
        foreach (var rule in new[] { "P-37", "R-09" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        // The category: the Controller opens its account, the Contador prepares it, the Controller approves it.
        var account = Ref(await controller.OkAsync(c, "finance", "create-account", new { code = "63300", name = "Teléfono e internet", accountClass = "EXPENSE", isControl = false }));
        var category = Ref(await contador.OkAsync(c, "procurement", "prepare-expense-category", new
        {
            code = "TELEFONO",
            name = "Teléfono e internet",
            accountId = account,
            goodsType606 = "02",
            lineClass = "SERVICE",
        }));
        var approval = await controller.OkAsync(c, "procurement", "approve-expense-categories", new { expenseCategoryIds = new[] { category } });
        Assert.Equal(1, approval.GetProperty("result").GetProperty("approved").GetInt32());
        var types = await clerk.GetOkAsync($"/api/v1/companies/{c}/tax/purchase-tax-types?date={today:yyyy-MM-dd}");
        var telecom = types.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("code").GetString() == "TELECOM").GetProperty("taxTypeId").GetGuid();

        // What the screen shows while it is typed (E-GAS-07-6), then the bill registered and matched.
        var lines = new[] { new { description = "Factura de teléfono de septiembre", expenseCategoryId = category, taxTypeId = telecom, quantity = "1", unitPrice = "30000.00" } };
        var previewResponse = await clerk.PostAsync(
            $"/api/v1/companies/{c}/procurement/expense-invoices/preview", new StringContent(JsonSerializer.Serialize(new { orderDate = today, lines }), Encoding.UTF8, "application/json"));
        var preview = JsonDocument.Parse(await previewResponse.EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("30000.00|9000.00|39000.00", $"{preview.GetProperty("netTotal").GetString()}|{preview.GetProperty("taxTotal").GetString()}|{preview.GetProperty("total").GetString()}");
        var si = Ref(await clerk.OkAsync(c, "procurement", "register-expense-invoice", new
        {
            partyId = r.Purchasing.SupplierId,
            supplierFiscalNumber = "B0100000801",
            docDate = today,
            dueDate = today.AddDays(30),
            plantId = r.Purchasing.PlantId,
            lines,
        }));
        var match = await clerk.OkAsync(c, "procurement", "match-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 1 });
        Assert.Equal("MATCH_EXCEPTION|39000.00", $"{match.GetProperty("result").GetProperty("status").GetString()}|{match.GetProperty("result").GetProperty("total").GetString()}");
        await controller.OkAsync(c, "procurement", "approve-match-exception", new { supplierInvoiceId = si, expectedVersion = 2, reason = "Factura de teléfono del mes" });
        var posted = await clerk.OkAsync(c, "procurement", "post-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 3 });
        Assert.Equal("POSTED|39000.00", $"{posted.GetProperty("result").GetProperty("accountingStatus").GetString()}|{posted.GetProperty("result").GetProperty("payable").GetString()}");
        Assert.Equal("5400.00|3000.00|600.00|-39000.00", await h.ScalarAsync<string>(
            $"SELECT {Role("ITBIS_RECOVERABLE")} || '|' || {Role("SELECTIVE_TAX_EXPENSE")} || '|' || {Role("OTHER_TAX_EXPENSE")} || '|' || {Role("AP_CONTROL")}"));

        // GAS-15: paid as any supplier invoice — proposal, prepared, released (R-09), matched with the statement.
        var bank = Ref(await controller.OkAsync(c, "treasury", "register-bank-account", new { bankCode = "TEST_BANK", accountNumber = "0123456789", glAccountCode = "1101" }));
        await h.VerifiedPartyBankAccountAsync(r.Purchasing.SupplierId, treasurerSession, controllerSession, 1, "9876543210", 73);
        var proposal = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/payment-proposal?dueUntil={today.AddDays(30):yyyy-MM-dd}");
        var supplier = Assert.Single(proposal.GetProperty("suppliers").EnumerateArray());
        var invoice = Assert.Single(supplier.GetProperty("invoices").EnumerateArray());
        var payment = Ref(await treasurer.OkAsync(c, "treasury", "prepare-supplier-payment", new
        {
            partyId = r.Purchasing.SupplierId,
            bankAccountId = bank,
            partyBankAccountId = supplier.GetProperty("partyBankAccountId").GetGuid(),
            valueDate = today,
            bankReference = "TRF-TEL",
            applications = new[] { new { apDocId = invoice.GetProperty("apDocId").GetGuid(), amount = "39000.00" } },
        }));
        await controller.OkAsync(c, "treasury", "release-supplier-payment", new { paymentId = payment, expectedVersion = 1 });
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{today:dd/MM/yyyy},TRF-TEL,Transferencia PAG-000001,39000.00,\n";
        var statement = Ref(await treasurer.OkAsync(c, "treasury", "import-bank-statement", new
        {
            bankAccountId = bank,
            fileName = "extracto.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
            periodFrom = today,
            periodTo = today,
            openingBalance = "0.00",
            closingBalance = "-39000.00",
        }));
        var suggestions = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-statements/{statement}/match-suggestions");
        var line = Assert.Single(suggestions.GetProperty("lines").EnumerateArray());
        await treasurer.OkAsync(c, "treasury", "match-bank-line", new { lineId = line.GetProperty("lineId").GetGuid(), expectedLineVersion = 1, paymentId = payment, expectedPaymentVersion = 2 });
        var paid = await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/payments/{payment}");
        Assert.Equal("CLEARED", paid.GetProperty("status").GetString());
        Assert.Equal("0.00", await h.ScalarAsync<string>($"SELECT {Role("AP_CONTROL")}::text"));

        // The month's 606 and its reconciliations.
        var period = today.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var report = await contador.GetOkAsync($"/api/v1/companies/{c}/tax/reports/606?period={period}");
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        Assert.Equal($"NCF|B0100000801|02|{today:yyyy-MM-dd}|30000.00|0.00|30000.00|5400.00|5400.00|3000.00|600.00|0.00|2", string.Join('|', new[]
        {
            "recordKind", "ncf", "goodsType", "paymentDate", "servicesAmount", "goodsAmount", "totalAmount", "itbisBilled", "itbisToAdvance", "selectiveTax", "otherTaxes", "legalTip",
            "paymentMethod",
        }.Select(p => record.GetProperty(p).ToString())));
        var run = await controller.OkAsync(c, "reconciliation", "run-reconciliation", new { reconCodes = new[] { "AP-GL", "TAX-606" } });
        Assert.Equal(
            "AP-GL:MATCHED:0,TAX-606:MATCHED:0",
            string.Join(',', run.GetProperty("result").GetProperty("runs").EnumerateArray()
                .Select(x => $"{x.GetProperty("code").GetString()}:{x.GetProperty("status").GetString()}:{x.GetProperty("warnings").GetInt32()}").Order(StringComparer.Ordinal)));
    }
}
