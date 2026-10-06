using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Time;
using Rochell.Procurement.Expenses;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// USD-1 E2E-U1 through the API (E-USD1-07-7), with the baseline's figures. Last month, at 60.00: the order and invoice of a forklift (USD 8,000)
/// and spare parts (USD 2,000) — 600,000.00 —, the freight (USD 1,000 = 60,000.00), the customs agent (15,000.00 + ITBIS) and the DUA
/// (duties 30,000.00, ITBIS 124,200.00); the settlement spreads 105,000.00: the forklift costs 564,000.00 and the parts 141,000.00. The month
/// closes at 60.50: an unrealized loss of 5,500.00 on the open USD 11,000, reversed the next day. Today the goods invoice is paid from the
/// peso account at the bank's 61.00: 610,000.00 out of the bank, a realized loss of 10,000.00.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class UsdAcceptanceTests(PostgresFixture postgres)
{
    private static readonly string[] Rules = ["P-37", "P-38", "P-39", "P-40", "P-41", "P-43", "P-43R"];

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    private static string D(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Trait("AcceptanceUsd1", "E2E-U1")]
    [Fact]
    public async Task E2EU1_rate_order_invoice_DUA_settlement_revaluation_and_payment_with_exchange_difference_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);
        var end = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var rateDay = end.DayOfWeek switch { DayOfWeek.Saturday => end.AddDays(-1), DayOfWeek.Sunday => end.AddDays(-2), _ => end };
        var day = rateDay.AddDays(-7);

        // Deployment: the purchasing setup (plant, local supplier, policies, posting), the ITBIS 18 % tax type, the USD accounts and rules.
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.OpenPeriodsAsync(end.Year);
        var p = s.Purchasing;
        await h.ActivateRuleAsync(await h.FiscalActorsAsync(initEnvironment: false), "itbis_18", "ITBIS_18", FiscalRuleKinds.PurchaseTaxType,
            """{"label":"ITBIS 18 %","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"}]}""", new DateOnly(2026, 1, 1));
        var itbis18 = await h.ScalarAsync<Guid>("SELECT rule_id FROM tax.fiscal_rule WHERE code = 'ITBIS_18'");
        foreach (var (role, code, control) in new[]
        {
            ("AP_FOREIGN", "21020", true), ("IMPORT_CLEARING", "13900", true), ("FX_LOSS", "68100", false), ("FX_GAIN", "48100", false), ("FX_UNREALIZED", "68200", false),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}' FROM fin.posting_rule r "
            + $"WHERE r.posting_rule_id = v.posting_rule_id AND v.version = 1 AND r.code IN ({string.Join(',', Rules.Select(r => $"'{r}'"))})");
        var contadorSession = await h.SessionWithRolesAsync("CONTADOR");
        var categories = new Dictionary<string, Guid>();
        foreach (var (code, account, cls, type, lineClass) in new[]
        {
            ("MONTACARGAS", "15300", "ASSET", "04", "GOODS"), ("REPUESTOS", "66150", "EXPENSE", "02", "GOODS"), ("FLETE", "63810", "EXPENSE", "09", "SERVICE"),
            ("GESTION_ADUANAL", "63820", "EXPENSE", "02", "SERVICE"),
        })
        {
            var accountId = (await h.RunAsync(new CreateAccount(c, p.Controller, "acc-" + code, account, code, cls, false), new CreateAccountHandler())).ResultRef;
            categories[code] = (await h.RunAsync(new PrepareExpenseCategory(c, contadorSession, "cat-" + code, code, code, accountId, type, lineClass), new PrepareExpenseCategoryHandler()))
                .ResultRef;
        }

        await h.RunAsync(new ApproveExpenseCategories(c, p.Controller, "cats", [.. categories.Values]), new ApproveExpenseCategoriesHandler());
        await h.CreateAccountAsync("1101", "Banco en pesos", isControl: true);
        var pesoBank = (await h.RunAsync(new RegisterBankAccount(c, p.Controller, "bank", "BPD", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;

        var treasurer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("TESORERO"));
        var controller = await api.SignInAsSessionUserAsync(p.Controller);
        var buyer = await api.SignInAsSessionUserAsync(p.Buyer);
        var payables = await api.SignInAsSessionUserAsync(s.Clerk);
        var contador = await api.SignInAsSessionUserAsync(contadorSession);

        // Rates: last month at 60.00 and 60.50, today 61.00 (Tesorería enters, the Controller approves).
        foreach (var (date, rate) in new[] { (day, "60.0000"), (rateDay, "60.5000"), (today, "61.0000") })
        {
            var r = Ref(await treasurer.OkAsync(c, "finance", "prepare-exchange-rate", new { currency = "USD", rateDate = D(date), rate, source = "Banco Central" }));
            await controller.OkAsync(c, "finance", "approve-exchange-rate", new { rateId = r, expectedVersion = 1 });
        }

        // The foreign suppliers: the forklift's seller and the ocean carrier.
        async Task<Guid> ForeignAsync(string name, string country)
        {
            var id = Ref(await buyer.OkAsync(c, "master-data", "create-foreign-supplier", new { legalName = name, country }));
            await controller.OkAsync(c, "master-data", "activate-supplier", new { partyId = id, expectedVersion = 1 });
            return id;
        }

        var seller = await ForeignAsync("Forklift Parts Inc.", "US");
        var carrier = await ForeignAsync("Ocean Freight Ltd.", "PA");
        await h.VerifiedPartyBankAccountAsync(seller, await h.SessionWithRolesAsync("TESORERO"), p.Controller, 1, "US64SVBKUS6S3300958879", 73);

        // The order in USD: approved on its peso value at today's rate (USD 10,000 × 61 = 610,000.00, the Controller's).
        var po = Ref(await buyer.OkAsync(c, "procurement", "create-expense-purchase-order", new
        {
            plantId = p.PlantId,
            partyId = seller,
            orderDate = D(day),
            lines = new object[]
            {
                new { description = "Montacargas usado Toyota 8FGU25", expenseCategoryId = categories["MONTACARGAS"], taxTypeId = (Guid?)null, quantity = "1", unitPrice = "8000" },
                new { description = "Repuestos de montacargas", expenseCategoryId = categories["REPUESTOS"], taxTypeId = (Guid?)null, quantity = "4", unitPrice = "500" },
            },
        }));
        await buyer.OkAsync(c, "procurement", "submit-purchase-order", new { plantId = p.PlantId, purchaseOrderId = po, expectedVersion = 1 });
        await controller.OkAsync(c, "procurement", "approve-purchase-order", new { plantId = p.PlantId, purchaseOrderId = po, expectedVersion = 2 });
        var order = await controller.GetOkAsync($"/api/v1/companies/{c}/procurement/purchase-orders/{po}");
        var poLines = order.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("poLineId").GetGuid()).ToList();

        // The invoices: the goods (citing the order), the freight (no order, above the approval amount) and the customs agent (local, ITBIS).
        async Task<Guid> InvoiceAsync(Guid party, string number, object[] lines, Guid? order = null, bool exception = false)
        {
            var si = Ref(await payables.OkAsync(c, "procurement", "register-expense-invoice", new
            {
                partyId = party,
                supplierFiscalNumber = number,
                docDate = D(day),
                dueDate = D(day.AddDays(60)),
                plantId = p.PlantId,
                lines,
                purchaseOrderId = order,
            }));
            await payables.OkAsync(c, "procurement", "match-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 1 });
            var version = 2;
            if (exception)
            {
                await controller.OkAsync(c, "procurement", "approve-match-exception", new { supplierInvoiceId = si, expectedVersion = 2, reason = "Importación del montacargas" });
                version = 3;
            }

            await payables.OkAsync(c, "procurement", "post-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = version });
            return si;
        }

        var goods = await InvoiceAsync(seller, "INV-2026-0147",
        [
            new { description = "Montacargas usado Toyota 8FGU25", expenseCategoryId = categories["MONTACARGAS"], taxTypeId = (Guid?)null, quantity = "1", unitPrice = "8000", purchaseOrderLineId = poLines[0] },
            new { description = "Repuestos de montacargas", expenseCategoryId = categories["REPUESTOS"], taxTypeId = (Guid?)null, quantity = "4", unitPrice = "500", purchaseOrderLineId = poLines[1] },
        ], po);
        var freight = await InvoiceAsync(carrier, "BL-778",
            [new { description = "Flete marítimo Miami–Caucedo", expenseCategoryId = categories["FLETE"], taxTypeId = (Guid?)null, quantity = "1", unitPrice = "1000" }], exception: true);
        var agent = await InvoiceAsync(p.SupplierId, "B0100000950",
            [new { description = "Gestión aduanal del embarque", expenseCategoryId = categories["GESTION_ADUANAL"], taxTypeId = itbis18, quantity = "1", unitPrice = "15000" }]);

        // The DUA, owed to the DGA; then the settlement prepared by Cuentas por pagar and approved by the Controller.
        var dua = Ref(await payables.OkAsync(c, "procurement", "register-customs-declaration", new
        {
            partyId = p.SupplierId,
            plantId = p.PlantId,
            duaNo = "10020-IM-2609-000123",
            duaDate = D(day),
            dueDate = D(day.AddDays(5)),
            cifAmount = "660000",
            dutiesAmount = "30000",
            itbisAmount = "124200",
            otherAmount = "0",
        }));
        var settlement = Ref(await payables.OkAsync(c, "procurement", "prepare-import-settlement", new
        {
            plantId = p.PlantId,
            settlementDate = D(rateDay),
            reference = "Embarque del montacargas",
            documents = new object[]
            {
                new { kind = "SUPPLIER_INVOICE", documentId = goods }, new { kind = "EXPENSE_INVOICE", documentId = freight },
                new { kind = "EXPENSE_INVOICE", documentId = agent }, new { kind = "CUSTOMS_DECLARATION", documentId = dua },
            },
        }));
        await controller.OkAsync(c, "procurement", "approve-import-settlement", new { settlementId = settlement, expectedVersion = 1 });
        var allocation = (await payables.GetOkAsync($"/api/v1/companies/{c}/procurement/import-settlements/{settlement}")).GetProperty("allocation");

        // Month-end revaluation by the Contador at 60.50, then today's payment of the goods from the peso account at the bank's 61.00.
        var revaluation = (await contador.OkAsync(c, "finance", "post-fx-revaluation", new { month = D(end) })).GetProperty("result");
        var goodsAp = await h.ScalarAsync<Guid>("SELECT ap_doc_id FROM fin.ap_document WHERE source_doc_id = @s", ("s", goods));
        var supplierAccount = await h.ScalarAsync<Guid>("SELECT party_bank_account_id FROM md.party_bank_account WHERE party_id = @p", ("p", seller));
        var payment = Ref(await treasurer.OkAsync(c, "treasury", "prepare-supplier-payment", new
        {
            partyId = seller,
            bankAccountId = pesoBank,
            partyBankAccountId = supplierAccount,
            valueDate = D(today),
            bankReference = "SWIFT 7781",
            applications = new[] { new { apDocId = goodsAp, amount = "10000" } },
            exchangeRate = "61.0000",
        }));
        await controller.OkAsync(c, "treasury", "release-supplier-payment", new { paymentId = payment, expectedVersion = 1 });
        var paid = await controller.GetOkAsync($"/api/v1/companies/{c}/treasury/payments/{payment}");

        Assert.Equal(
            "Montacargas usado Toyota 8FGU25:84000.0000=564000.0000,Repuestos de montacargas:21000.0000=141000.0000",
            string.Join(',', allocation.EnumerateArray().Select(a => $"{a.GetProperty("description").GetString()}:{a.GetProperty("addedCost").GetString()}={a.GetProperty("totalCost").GetString()}")));
        Assert.Equal("60.5000|5500.00", $"{revaluation.GetProperty("rate").GetString()}|{revaluation.GetProperty("loss").GetString()}");
        Assert.Equal("610000.0000|10000.0000|61.0000", $"{paid.GetProperty("amount").GetString()}|{paid.GetProperty("amountUsd").GetString()}|{paid.GetProperty("exchangeRate").GetString()}");
        Assert.Equal(
            "1101:-610000.00,13900:0.00,15300:564000.00,21020:-60000.00,63810:0.00,63820:0.00,66150:141000.00,68100:10000.00,68200:0.00",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
                FROM fin.account a WHERE a.code IN ('1101', '13900', '15300', '21020', '63810', '63820', '66150', '68100', '68200')
                """));
        Assert.Equal(
            "USD 0.00/USD 1000.00",
            await h.ScalarAsync<string>(
                $"SELECT 'USD ' || (SELECT open_amount_fc::numeric(19,2) FROM fin.ap_document WHERE source_doc_id = '{goods}') || '/USD ' || (SELECT open_amount_fc::numeric(19,2) FROM fin.ap_document WHERE source_doc_id = '{freight}')"));
    }
}
