using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Time;
using Rochell.Procurement.Expenses;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// AF-1 E2E-AF1 through the API (E-AF1-05-9). Two months ago, at 60.00: a used forklift for USD 8,000.00 (480,000.00) and its ocean
/// freight USD 1,400.00 (84,000.00), settled onto the forklift — 564,000.00 —; the class «Montacargas y equipos» (60 months, 10 %) is
/// approved and the forklift goes into service. Last month is depreciated: 507,600.00 ÷ 60 = 8,460.00. Today it is sold for 550,000.00:
/// book value 555,540.00, a loss of 5,540.00; FA-GL squares.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FixedAssetAcceptanceTests(PostgresFixture postgres)
{
    private static readonly string[] Rules = ["P-38", "P-40", "P-44", "P-45"];

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    private static string D(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Trait("AcceptanceAf1", "E2E-AF1")]
    [Fact]
    public async Task E2EAF1_class_invoice_settlement_service_depreciation_sale_and_reconciliation_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var day = thisMonth.AddMonths(-2).AddDays(9);
        var lastMonth = thisMonth.AddMonths(-1);

        // Deployment: purchasing setup, the foreign payable, the rules, the disposal accounts.
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.OpenPeriodsAsync(day.Year);
        await h.OpenPeriodsAsync(today.Year);
        var p = s.Purchasing;
        foreach (var (role, code, control) in new[]
        {
            ("AP_FOREIGN", "21020", true), ("ASSET_SALE_RECEIVABLE", "13800", false), ("ASSET_DISPOSAL_GAIN", "71500", false), ("ASSET_DISPOSAL_LOSS", "81500", false),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}' FROM fin.posting_rule r "
            + $"WHERE r.posting_rule_id = v.posting_rule_id AND v.version = 1 AND r.code IN ({string.Join(',', Rules.Select(r => $"'{r}'"))})");
        var contadorSession = await h.SessionWithRolesAsync("CONTADOR");
        var accounts = new Dictionary<string, Guid>();
        foreach (var (code, cls) in new[] { ("15300", "ASSET"), ("15390", "ASSET"), ("64100", "EXPENSE"), ("63810", "EXPENSE") })
        {
            accounts[code] = (await h.RunAsync(new CreateAccount(c, p.Controller, "acc-" + code, code, code, cls, false), new CreateAccountHandler())).ResultRef;
        }

        var forklift = (await h.RunAsync(
            new PrepareExpenseCategory(c, contadorSession, "cat-m", "MONTACARGAS", "Montacargas y equipos", accounts["15300"], "04", "GOODS"), new PrepareExpenseCategoryHandler())).ResultRef;
        var freight = (await h.RunAsync(
            new PrepareExpenseCategory(c, contadorSession, "cat-f", "FLETE", "Flete internacional", accounts["63810"], "09", "SERVICE"), new PrepareExpenseCategoryHandler())).ResultRef;
        await h.RunAsync(new ApproveExpenseCategories(c, p.Controller, "cats", [forklift, freight]), new ApproveExpenseCategoriesHandler());

        var treasurer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("TESORERO"));
        var controller = await api.SignInAsSessionUserAsync(p.Controller);
        var buyer = await api.SignInAsSessionUserAsync(p.Buyer);
        var payables = await api.SignInAsSessionUserAsync(s.Clerk);
        var contador = await api.SignInAsSessionUserAsync(contadorSession);

        // The class, prepared by the Contador and approved by the Controller.
        var assetClass = Ref(await contador.OkAsync(c, "fixed-assets", "prepare-asset-class", new
        {
            expenseCategoryId = forklift,
            usefulLifeMonths = 60,
            residualPct = "10",
            accumulatedAccountId = accounts["15390"],
            expenseAccountId = accounts["64100"],
        }));
        await controller.OkAsync(c, "fixed-assets", "approve-asset-class", new { assetClassId = assetClass, expectedVersion = 1 });

        // Two months ago at 60.00: the forklift's invoice and its freight, settled onto the forklift.
        var rate = Ref(await treasurer.OkAsync(c, "finance", "prepare-exchange-rate", new { currency = "USD", rateDate = D(day), rate = "60.0000", source = "Banco Central" }));
        await controller.OkAsync(c, "finance", "approve-exchange-rate", new { rateId = rate, expectedVersion = 1 });
        async Task<Guid> SupplierAsync(string name, string country)
        {
            var id = Ref(await buyer.OkAsync(c, "master-data", "create-foreign-supplier", new { legalName = name, country }));
            await controller.OkAsync(c, "master-data", "activate-supplier", new { partyId = id, expectedVersion = 1 });
            return id;
        }

        async Task<Guid> InvoiceAsync(Guid party, string number, Guid category, string description, string price)
        {
            var si = Ref(await payables.OkAsync(c, "procurement", "register-expense-invoice", new
            {
                partyId = party,
                supplierFiscalNumber = number,
                docDate = D(day),
                dueDate = D(day.AddDays(60)),
                plantId = p.PlantId,
                lines = new object[] { new { description, expenseCategoryId = category, taxTypeId = (Guid?)null, quantity = "1", unitPrice = price } },
            }));
            await payables.OkAsync(c, "procurement", "match-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 1 });
            await controller.OkAsync(c, "procurement", "approve-match-exception", new { supplierInvoiceId = si, expectedVersion = 2, reason = "Importación del montacargas" });
            await payables.OkAsync(c, "procurement", "post-supplier-invoice", new { supplierInvoiceId = si, expectedVersion = 3 });
            return si;
        }

        var goods = await InvoiceAsync(await SupplierAsync("Forklift Parts Inc.", "US"), "INV-2026-0147", forklift, "Montacargas usado Toyota 8FGU25", "8000");
        var shipping = await InvoiceAsync(await SupplierAsync("Ocean Freight Ltd.", "PA"), "BL-778", freight, "Flete marítimo Miami–Caucedo", "1400");
        var born = (await contador.GetOkAsync($"/api/v1/companies/{c}/fixed-assets/assets")).GetProperty("items")[0];
        var assetId = born.GetProperty("assetId").GetGuid();
        var settlement = Ref(await payables.OkAsync(c, "procurement", "prepare-import-settlement", new
        {
            plantId = p.PlantId,
            settlementDate = D(day),
            reference = "Embarque del montacargas",
            documents = new object[] { new { kind = "SUPPLIER_INVOICE", documentId = goods }, new { kind = "EXPENSE_INVOICE", documentId = shipping } },
        }));
        await controller.OkAsync(c, "procurement", "approve-import-settlement", new { settlementId = settlement, expectedVersion = 1 });

        // Into service two months ago; last month depreciated after its preview.
        await contador.OkAsync(c, "fixed-assets", "put-fixed-asset-in-service", new
        {
            assetId,
            expectedVersion = 2,
            inServiceOn = D(day),
            plantId = p.PlantId,
            responsible = "Encargado de almacén",
        });
        var preview = await contador.GetOkAsync($"/api/v1/companies/{c}/fixed-assets/depreciation-preview?month={D(lastMonth)}");
        var depreciation = (await contador.OkAsync(c, "fixed-assets", "post-depreciation", new { month = D(lastMonth) })).GetProperty("result");

        // Sold today: the Contador prepares, the Controller sees the loss and approves.
        var disposal = Ref(await contador.OkAsync(c, "fixed-assets", "prepare-asset-disposal", new
        {
            assetId,
            kind = "SALE",
            disposalDate = D(today),
            price = "550000",
            reason = "Venta del montacargas a Ferretería del Este",
        }));
        var draft = (await controller.GetOkAsync($"/api/v1/companies/{c}/fixed-assets/disposals?status=DRAFT")).GetProperty("items")[0];
        await controller.OkAsync(c, "fixed-assets", "approve-asset-disposal", new { disposalId = disposal, expectedVersion = 1 });
        var card = await contador.GetOkAsync($"/api/v1/companies/{c}/fixed-assets/assets/{assetId}");
        await controller.OkAsync(c, "reconciliation", "run-reconciliation", new { reconCodes = new[] { "FA-GL" }, cutoffDate = D(today) });
        var findings = await h.ScalarAsync<long>(
            "SELECT count(*) FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id) WHERE r.recon_code = 'FA-GL' AND x.severity = 'ERROR'");

        Assert.Equal("AWAITING_SERVICE|480000.00", $"{born.GetProperty("status").GetString()}|{born.GetProperty("cost").GetString()}");
        Assert.Equal(
            $"{lastMonth:yyyy-MM}|True|False|8460.00|1",
            $"{preview.GetProperty("month").GetString()}|{preview.GetProperty("ended").GetBoolean()}|{preview.GetProperty("alreadyPosted").GetBoolean()}|{preview.GetProperty("total").GetString()}|{preview.GetProperty("lines").GetArrayLength()}");
        Assert.Equal("8460.00", depreciation.GetProperty("total").GetString());
        Assert.Equal("555540.00|0.00|5540.00", $"{draft.GetProperty("bookValue").GetString()}|{draft.GetProperty("gain").GetString()}|{draft.GetProperty("loss").GetString()}");
        Assert.Equal(
            "DISPOSED|564000.00|8460.00",
            $"{card.GetProperty("asset").GetProperty("status").GetString()}|{card.GetProperty("asset").GetProperty("cost").GetString()}|{card.GetProperty("asset").GetProperty("accumulated").GetString()}");
        Assert.Equal(
            "ACQUISITION,COST_ADDED,IN_SERVICE,DEPRECIATION,DISPOSAL",
            string.Join(',', card.GetProperty("movements").EnumerateArray().Select(m => m.GetProperty("kind").GetString())));
        Assert.Equal(0L, findings);
        Assert.Equal(
            "13800:550000.00,15300:0.00,15390:0.00,64100:8460.00,81500:5540.00",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
                FROM fin.account a WHERE a.code IN ('13800', '15300', '15390', '64100', '81500')
                """));
    }
}
