using System.Text.Json;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation;
using Rochell.Tax.Reports;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// GAS1-06 (E-GAS-06-1…6, GAS-14): the 606 of expense invoices — the type of the largest line's category, services and goods apart,
/// ITBIS without the other taxes, selective tax / other taxes / tip in fields 20–22 — and TAX-606 comparing them with their accounts.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExpenseReport606Tests(PostgresFixture postgres)
{
    [Trait("AcceptanceGas1", "GAS-14")]
    [Fact]
    public async Task GAS14_expense_invoices_report_their_categorys_type_services_and_goods_apart_and_each_tax_in_its_field()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var supplier = w.S.Purchasing.SupplierId;
        Task<Guid> Register(string key, string ncf, params Rochell.Procurement.SupplierInvoices.ExpenseLineInput[] lines)
            => h.RunAsync(new RegisterExpenseInvoice(h.CompanyId, w.Clerk, key, supplier, ncf, Today(h), Today(h).AddDays(30), w.Plant, lines), new RegisterExpenseInvoiceHandler())
                .ContinueWith(t => t.Result.ResultRef, TaskScheduler.Default);
        var month = await Register(
            "a", "B0100000901",
            Line(w, "Teléfono", "TELEFONO", "TELECOM", 1m, 5000m),
            Line(w, "Póliza de la flota", "SEGUROS", "SEGUROS", 1m, 20000m),
            Line(w, "Almuerzo con cliente", "REPRESENTACION", "CONSUMO_PROPINA", 1m, 2000m),
            Line(w, "Gasoil", "COMBUSTIBLE", "EXENTO", 30m, 100m));
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, w.Clerk, "am", month, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new ApproveMatchException(h.CompanyId, w.Controller, "aa", month, 2, "Gastos del mes"), new ApproveMatchExceptionHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "ap", month, 3), new PostSupplierInvoiceHandler());
        var parts = await Register("b", "B0100000902", Line(w, "Filtros", "REPUESTOS", "ITBIS_18", 2m, 500m));
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, w.Clerk, "bm", parts, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "bp", parts, 2), new PostSupplierInvoiceHandler());

        var period = Today(h).ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        var report = JsonDocument.Parse(await h.QueryAsync(new GetReport606(h.CompanyId, await h.SessionWithRolesAsync("CONTADOR"), period), new GetReport606Handler())).RootElement;
        var recon = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, w.Controller, "recon", ["TAX-606"]), new RunReconciliationHandler())).ResultPayload).RootElement;

        // The month's invoice: largest line the insurance (type 11); services 5,000 + 20,000 + 2,000; goods the fuel 3,000; ITBIS
        // 900 + 360; ISC 500 + 3,200; CDT 100; tip 200. The parts invoice: type 02, goods 1,000.00, ITBIS 180.00.
        string Fields(JsonElement r) => string.Join('|', new[]
        {
            "ncf", "goodsType", "servicesAmount", "goodsAmount", "totalAmount", "itbisBilled", "itbisToAdvance", "selectiveTax", "otherTaxes", "legalTip",
        }.Select(n => r.GetProperty(n).ValueKind == JsonValueKind.String ? r.GetProperty(n).GetString() : r.GetProperty(n).ToString()));
        Assert.Equal(
            "B0100000901|11|27000.00|3000.00|30000.00|1260.00|1260.00|3700.00|100.00|200.00,B0100000902|02|0.00|1000.00|1000.00|180.00|180.00|0.00|0.00|0.00",
            string.Join(',', report.GetProperty("records").EnumerateArray().Select(Fields)));
        Assert.All(report.GetProperty("records").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("warnings").GetArrayLength()));
        Assert.Equal("MATCHED", recon.GetProperty("runs")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task TAX606_warns_when_a_selective_tax_posted_in_the_month_is_not_in_the_606()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var si = (await h.RunAsync(
            new RegisterExpenseInvoice(h.CompanyId, w.Clerk, "a", w.S.Purchasing.SupplierId, "B0100000903", Today(h), Today(h), w.Plant, [Line(w, "Póliza", "SEGUROS", "SEGUROS", 1m, 10000m)]),
            new RegisterExpenseInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, w.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());

        // Tampered by the owner role, as a defect would leave it: the invoice's selective tax gone from its determination.
        await h.AdminRequireAsync("SET LOCAL session_replication_role = replica; DELETE FROM tax.tax_determination_line WHERE effect = 'SELECTIVE_TAX'");
        await h.RunAsync(new RunReconciliation(h.CompanyId, w.Controller, "recon", ["TAX-606"]), new RunReconciliationHandler());

        Assert.Equal(
            $"selective:{Today(h):yyyyMM}:TAX606_SELECTIVE_DIFFERENCE:0.00:1600.00",
            await h.ScalarAsync<string>(
                "SELECT string_agg(match_key || ':' || classification || ':' || value_a::numeric(19,2) || ':' || value_b::numeric(19,2), ',') FROM rec.recon_exception"));
    }
}
