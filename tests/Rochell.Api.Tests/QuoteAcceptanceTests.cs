using System.Text;
using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// QUO-1 E2E-Q1 through the API (E-QUO1-04-9): quote with a special price → approved by the policy approver → sent → printed →
/// converted → the order at the quoted price, confirmed by credit. The deployment and masters are those of E2E-S1.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QuoteAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptanceQuo1", "E2E-Q1")]
    [Fact]
    public async Task E2EQ1_quote_special_price_approved_sent_printed_converted_and_the_order_confirmed_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

        // Deployment: periods, plant and locations, account maps, policies, the active SALES_ITBIS rule.
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        var patio = await h.CreateLocationAsync(plant, "PATIO");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        foreach (var (role, code, control) in new[]
        {
            ("FINISHED_GOODS", "1350", true), ("FINISHED_GOODS_IN_TRANSIT", "1351", true), ("MIGRATION_CLEARING", "3990", false), ("COGS", "5100", false),
            ("CONTRACT_ASSET", "1240", true), ("REVENUE_PRODUCT", "4100", false), ("TRANSIT_LOSS", "6900", false), ("AR_CONTROL", "1210", true),
            ("ITBIS_PAYABLE", "2150", false), ("UNAPPLIED_RECEIPTS", "2120", true),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.CreateActivePolicyAsync("CREDIT", new Dictionary<string, string>
        {
            ["overdue_days_block"] = "30",
            ["ar_aging_bucket_1_days"] = "30",
            ["ar_aging_bucket_2_days"] = "60",
            ["ar_aging_bucket_3_days"] = "90",
        });
        await h.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string>
        {
            ["unbilled_delivery_presentation"] = "CONTRACT_ASSET",
            ["unbilled_aging_alert_days"] = "30",
            ["delivery_open_alert_hours"] = "24",
        });
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));

        // The people, each signed in through OIDC.
        var controllerSession = await h.SessionWithRolesAsync("CONTROLLER");
        var controller = await api.SignInAsSessionUserAsync(controllerSession);
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var credit = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CREDITO"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));

        // The rules, approved by the Controller.
        foreach (var rule in new[] { "OPEN-INV" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        // Masters: the product, its standard cost and price, the opening stock, the customer, the truck and its driver.
        var block = Ref(await storekeeper.OkAsync(c, "master-data", "create-finished-good", new { code = "BLOQUE-6", description = "Bloque de 6 pulgadas", baseUom = "un", itemCategory = "BLOQUE" }));
        await controller.OkAsync(c, "master-data", "activate-item", new { itemId = block, expectedVersion = 1 });
        var cost = (await controller.OkAsync(c, "sales", "prepare-standard-cost", new { itemId = block, valuationAreaId = area, unitCost = "32.75" }))
            .GetProperty("result").GetProperty("costVersionId").GetGuid();
        await approver.OkAsync(c, "sales", "approve-standard-cost", new { costVersionId = cost });
        var opening = Ref(await controller.OkAsync(c, "sales", "prepare-opening-inventory", new
        {
            fileName = "apertura.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("planta,ubicacion,producto,cantidad,documento\nHIGUEY,PATIO,BLOQUE-6,2000,ADM-1\n")),
            cutoverDate = new DateOnly(today.Year, today.Month, 1),
        }));
        await approver.OkAsync(c, "sales", "post-opening-inventory", new { batchId = opening, expectedVersion = 1 });
        var prices = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new { lines = new[] { new { itemId = block, uom = "un", unitPrice = "50.00" } } }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = prices });
        var customer = Ref(await seller.OkAsync(c, "sales", "create-customer", new { rnc = "131925332", legalName = "Constructora Uno" }));
        var terms = (await credit.OkAsync(c, "sales", "prepare-customer-terms", new { partyId = customer, paymentTermsDays = 30, creditLimit = "1000000.00", creditHold = false }))
            .GetProperty("result").GetProperty("termsVersionId").GetGuid();
        await controller.OkAsync(c, "sales", "approve-customer-terms", new { termsVersionId = terms });
        await credit.OkAsync(c, "sales", "activate-customer", new { partyId = customer, expectedVersion = 1 });
        var truck = Ref(await dispatch.OkAsync(c, "sales", "register-vehicle", new { plate = "L123456", fleetCode = "BR 09", capacityKg = "12000" }));
        var driver = Ref(await dispatch.OkAsync(c, "sales", "register-driver", new { fullName = "Juan Pérez", nationalId = "00112345678" }));

        // The quote: 1,000 blocks at 45.00 (the list says 50.00) — a special price the Vendedor cannot send alone.
        var quote = Ref(await seller.OkAsync(c, "sales", "create-quote", new
        {
            partyId = customer,
            plantId = plant,
            validUntil = today.AddDays(15),
            deliveryTermCode = "DELIVERED_OWN_TRANSPORT",
            siteAddress = "Obra Punta Cana",
            customerRef = "OC-2026-15",
            notes = "Entrega en 5 días laborables",
            lines = new[] { new { itemId = block, uom = "un", quantity = "1000", unitPrice = (string?)"45.00" } },
        }));
        var refused = await seller.CommandAsync(c, "sales", "send-quote", new { quoteId = quote, expectedVersion = 1 }, Guid.NewGuid().ToString());
        Assert.Equal((System.Net.HttpStatusCode.UnprocessableEntity, "QUOTE_PRICE_APPROVAL_REQUIRED"), await refused.ProblemAsync());
        await seller.OkAsync(c, "sales", "submit-quote-for-approval", new { quoteId = quote, expectedVersion = 1 });
        var pending = await approver.GetOkAsync($"/api/v1/companies/{c}/sales/quotes?status=PENDING_APPROVAL");
        Assert.Equal("COT-000001", Assert.Single(pending.GetProperty("items").EnumerateArray()).GetProperty("quoteNo").GetString());
        await approver.OkAsync(c, "sales", "approve-quote-prices", new { quoteId = quote, expectedVersion = 2 });
        await seller.OkAsync(c, "sales", "send-quote", new { quoteId = quote, expectedVersion = 3 });

        // What the customer receives: 45,000.00 + ITBIS 8,100.00 = 53,100.00 (informative).
        var print = await seller.GetOkAsync($"/api/v1/companies/{c}/sales/quotes/{quote}/print");
        Assert.Equal("COT-000001|SENT|45000.00|8100.00|53100.00|Entrega en 5 días laborables", $"{print.GetProperty("quoteNo").GetString()}|{print.GetProperty("status").GetString()}|" +
            $"{print.GetProperty("netTotal").GetString()}|{print.GetProperty("itbisTotal").GetString()}|{print.GetProperty("total").GetString()}|{print.GetProperty("notes").GetString()}");

        // The customer accepts: the order is born from the quote at 45.00 and credit confirms it.
        var order = Ref(await seller.OkAsync(c, "sales", "convert-quote", new { quoteId = quote, expectedVersion = 4 }));
        await seller.OkAsync(c, "sales", "submit-for-credit", new { salesOrderId = order, expectedVersion = 1 });
        var detail = await seller.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}");
        Assert.Equal("CONFIRMED|45000.00|COT-000001|OC-2026-15|Obra Punta Cana|45.0000", $"{detail.GetProperty("header").GetProperty("status").GetString()}|" +
            $"{detail.GetProperty("header").GetProperty("totalNet").GetString()}|{detail.GetProperty("header").GetProperty("quoteNo").GetString()}|" +
            $"{detail.GetProperty("customerPoRef").GetString()}|{detail.GetProperty("siteAddress").GetString()}|{detail.GetProperty("lines")[0].GetProperty("unitPrice").GetString()}");
        var converted = await seller.GetOkAsync($"/api/v1/companies/{c}/sales/quotes/{quote}");
        Assert.Equal($"CONVERTED|{order}|True", $"{converted.GetProperty("header").GetProperty("status").GetString()}|{converted.GetProperty("salesOrderId").GetString()}|" +
            $"{converted.GetProperty("priceApprovalCurrent").GetBoolean()}");
        Assert.Equal("DRAFT,PENDING_APPROVAL,DRAFT,SENT,CONVERTED", string.Join(',', converted.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));

        // A quote posts nothing: no journal, no stock movement, no tax determination came from it.
        Assert.Equal("0|0", await h.ScalarAsync<string>(
            "SELECT (SELECT count(*) FROM tax.tax_determination WHERE subject_type = 'Quote')::text || '|' || (SELECT count(*) FROM core.domain_event WHERE aggregate_type = 'Quote' AND event_type LIKE '%Posted%')::text"));
    }
}
