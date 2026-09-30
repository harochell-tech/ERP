using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation;
using Rochell.Tax;
using Rochell.Tax.Reports;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Payments;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>FIS2-02: the 606 of a month, its CSV, the IR-17 summary and TAX-606 (E-FIS2-02-1…12).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Report606Tests(PostgresFixture postgres)
{
    /// <summary>ISR 2 % on the net withheld from companies, the 606's type 2 (honorarios por servicios — a test value).</summary>
    private const string IsrWithholding = """{"tax_code":"RET_ISR","rate":"0.02","base":"NET","party_types":["COMPANY"],"isr_withholding_type":"2"}""";

    private const string IsrWithholdingWithoutType = """{"tax_code":"RET_ISR","rate":"0.02","base":"NET","party_types":["COMPANY"]}""";

    private const string Classification = """{"classes":{"CEMENTO":"09","AGREGADO":"09","ADITIVO":"09","OTRA_MATERIA_PRIMA":"09"}}""";

    private sealed record World(TestInvoicing Inv, Guid Paid, Guid Credit, Guid Bank, Guid PartyAccount, Guid Treasurer, Guid Controller);

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static string Period(DateOnly day) => day.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Sand at 1,500 per t, ITBIS 18 %, ISR 2 %: invoice B0100000001 of 6 t (9,000.00 + 1,620.00 − 180.00) paid by transfer today, and
    /// B0100000002 of 4 t (6,000.00 + 1,080.00 − 120.00) on credit.
    /// </summary>
    private static async Task<World> WorldAsync(TestHarness h, string withholding = IsrWithholding, bool classify = true, decimal paidTons = 6m, decimal creditTons = 4m)
    {
        var inv = await h.CreateInvoicingSetupAsync(received: 10m);
        await h.EnableInvoicePostingAsync(withholdingDefinition: withholding);
        if (classify)
        {
            await h.ActivateRuleAsync(await h.FiscalActorsAsync(), "clasif", "CLASIF_606", FiscalRuleKinds.Report606Classification, Classification, new DateOnly(2026, 1, 1));
        }

        var paid = await PostAsync(h, inv, "B0100000001", paidTons);
        var credit = await PostAsync(h, inv, "B0100000002", creditTons);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{PaymentSetup.R09}' AND version = 1");
        await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
        var controller = inv.Purchasing.Controller;
        var bank = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, controller, "bank", "BPD", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var partyAccount = await h.VerifiedPartyBankAccountAsync(inv.Purchasing.SupplierId, treasurer, controller, 1, "9876543210", 73);
        var w = new World(inv, paid, credit, bank, partyAccount, treasurer, controller);
        await PayAsync(h, w, w.Paid, "pay-1", treasurer, controller);
        return w;
    }

    private static async Task<Guid> PostAsync(TestHarness h, TestInvoicing inv, string ncf, decimal tons)
    {
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, inv.Clerk, "si-" + ncf, inv.Purchasing.SupplierId, ncf, Today(h), Today(h).AddDays(30), [new(inv.PoLineId, tons, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, inv.Clerk, "m-" + ncf, si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, inv.Clerk, "p-" + ncf, si, 2), new PostSupplierInvoiceHandler());
        return si;
    }

    private static async Task PayAsync(TestHarness h, World w, Guid invoice, string key, Guid treasurer, Guid controller)
    {
        var apDoc = await h.ScalarAsync<Guid>("SELECT ap_doc_id FROM fin.ap_document WHERE source_doc_id = @s", ("s", invoice));
        var open = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", apDoc));
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, treasurer, key, w.Inv.Purchasing.SupplierId, w.Bank, w.PartyAccount, Today(h), null, [new(apDoc, open)]),
            new PrepareSupplierPaymentHandler())).ResultRef;
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, controller, key + "-rel", payment, 1), new ReleaseSupplierPaymentHandler());
    }

    private static async Task<JsonElement> ReportAsync(TestHarness h, Guid session, DateOnly day)
        => JsonDocument.Parse(await h.QueryAsync(new GetReport606(h.CompanyId, session, Period(day)), new GetReport606Handler())).RootElement;

    private static string Row(JsonElement r)
        => string.Join('|', new[]
        {
            "recordKind", "ncf", "idType", "goodsType", "paymentDate", "goodsAmount", "totalAmount", "itbisBilled", "itbisToCost", "itbisToAdvance", "itbisWithheld",
            "isrWithholdingType", "isrWithheld", "paymentMethod",
        }.Select(p => r.GetProperty(p).ValueKind == JsonValueKind.Null ? "-" : r.GetProperty(p).ToString()));

    [Trait("AcceptanceFis2", "F2-01")]
    [Trait("AcceptanceFis2", "F2-03")]
    [Trait("AcceptanceFis2", "F2-05")]
    [Fact]
    public async Task F201_the_606_of_the_month_carries_each_invoice_with_its_payment_withholding_and_method_and_its_CSV_follows_the_DGII_tool()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var fiscal = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");

        var report = await ReportAsync(h, fiscal, Today(h));
        var csv = Report606Csv.Build(await h.QueryAsync(new GetReport606(h.CompanyId, fiscal, Period(Today(h))), new GetReport606Handler()));
        var ir17 = JsonDocument.Parse(await h.QueryAsync(new GetIr17Summary(h.CompanyId, fiscal, Period(Today(h))), new GetIr17SummaryHandler())).RootElement;
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var denied = await Assert.ThrowsAsync<Platform.Commands.DomainException>(() => h.QueryAsync(new GetReport606(h.CompanyId, buyer, Period(Today(h))), new GetReport606Handler()));

        // 6 t × 1,500 = 9,000.00; ITBIS 18 % = 1,620.00; ISR 2 % = 180.00, paid today by transfer (2). 4 t = 6,000.00; ITBIS 1,080.00;
        // unpaid: no payment date, no withholding yet (E-FIS2-02-12), on credit (4).
        var today = Today(h).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            $"NCF|B0100000001|1|09|{today}|9000.00|9000.00|1620.00|0.00|1620.00|0.00|2|180.00|2;NCF|B0100000002|1|09|-|6000.00|6000.00|1080.00|0.00|1080.00|0.00|-|0.00|4",
            string.Join(';', report.GetProperty("records").EnumerateArray().Select(Row)));
        Assert.Equal($"{Period(Today(h))}|2|15000.00", $"{report.GetProperty("period").GetString()}|{report.GetProperty("recordCount").GetInt32()}|{report.GetProperty("totalAmount").GetString()}");

        var rnc = await h.ScalarAsync<string>("SELECT rnc FROM md.party WHERE party_id = @p", ("p", w.Inv.Purchasing.SupplierId));
        var day = Today(h).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            $"{rnc},1,09,B0100000001,,{day},{day},,9000.00,9000.00,1620.00,,,,1620.00,,2,180.00,,,,,2\r\n" +
            $"{rnc},1,09,B0100000002,,{day},,,6000.00,6000.00,1080.00,,,,1080.00,,,,,,,,4\r\n",
            csv);
        Assert.Equal("ISR:2:1:9000.00:180.00", string.Join('|', ir17.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("tax").GetString()}:{l.GetProperty("isrWithholdingType").GetString()}:{l.GetProperty("records").GetInt32()}:{l.GetProperty("base").GetString()}:{l.GetProperty("amount").GetString()}")));
        Assert.Equal(Platform.Commands.AuthorizationErrors.NotAuthorized, denied.Code);
    }

    [Trait("AcceptanceFis2", "F2-02")]
    [Fact]
    public async Task F202_a_reversed_invoice_is_not_reported_and_a_withholding_paid_next_month_is_reported_then_without_ITBIS()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await WorldAsync(h, paidTons: 5m, creditTons: 3m);
        var reversed = await PostAsync(h, w.Inv, "B0100000003", 2m);
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, w.Controller, "rev", reversed, 3, "NCF duplicado"), new ReverseSupplierInvoiceHandler());
        var month = Today(h);

        clock.Advance(TimeSpan.FromDays(35));
        var (treasurer, controller, fiscal) = (await h.SessionWithRolesAsync("TESORERO"), await h.SessionWithRolesAsync("CONTROLLER"), await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"));
        await PayAsync(h, w, w.Credit, "pay-2", treasurer, controller);
        var first = await ReportAsync(h, fiscal, month);
        var next = await ReportAsync(h, fiscal, Today(h));

        // The reversed B0100000003 is not reported; B0100000002 stays in its month as on credit, and comes back in the payment month
        // with its 90.00 ISR (3 t × 1,500 = 4,500.00 × 2 %) and no ITBIS, so the ITBIS is advanced once (E-FIS2-02-11).
        Assert.Equal("B0100000001:NCF:2|B0100000002:NCF:4", string.Join('|', first.GetProperty("records").EnumerateArray()
            .Select(r => $"{r.GetProperty("ncf").GetString()}:{r.GetProperty("recordKind").GetString()}:{r.GetProperty("paymentMethod").GetInt32()}")));
        Assert.Equal(
            $"PAYMENT|B0100000002|1|09|{Today(h):yyyy-MM-dd}|4500.00|4500.00|0.00|0.00|0.00|0.00|2|90.00|2",
            string.Join(';', next.GetProperty("records").EnumerateArray().Select(Row)));
    }

    [Trait("AcceptanceFis2", "F2-06")]
    [Fact]
    public async Task F206_TAX_606_warns_of_an_ITBIS_difference_a_missing_classification_and_an_ISR_withholding_without_its_type()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await WorldAsync(h, IsrWithholdingWithoutType, classify: false);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var clean = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "recon", ["TAX-606"]), new RunReconciliationHandler())).ResultPayload).RootElement;
        var findings = await h.ScalarAsync<string>("SELECT string_agg(x.classification || ':' || x.match_key, ',' ORDER BY x.match_key, x.classification) FROM rec.recon_exception x");
        await h.AdminRequireAsync(
            """
            BEGIN; SET LOCAL session_replication_role = replica;
            UPDATE fin.gl_entry SET debit = debit - 1
            WHERE gl_entry_id = (SELECT gl_entry_id FROM fin.gl_entry WHERE account_role = 'ITBIS_RECOVERABLE' AND debit > 0 ORDER BY debit LIMIT 1);
            COMMIT;
            """);
        await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "recon-2", ["TAX-606"]), new RunReconciliationHandler());

        Assert.Equal("TAX-606:EXCEPTIONS", string.Join(',', clean.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}")));
        Assert.Equal("CLASSIFICATION_MISSING:ncf:B0100000001,ISR_WITHHOLDING_TYPE_MISSING:ncf:B0100000001,CLASSIFICATION_MISSING:ncf:B0100000002", findings);
        // The 606 advances 1,620.00 + 1,080.00 = 2,700.00; the altered ledger holds 2,699.00.
        Assert.Equal("2700.00|2699.00", await h.ScalarAsync<string>(
            "SELECT x.value_a::numeric(19,2)::text || '|' || x.value_b::numeric(19,2)::text FROM rec.recon_exception x WHERE x.classification = 'TAX606_ITBIS_DIFFERENCE'"));
    }
}
