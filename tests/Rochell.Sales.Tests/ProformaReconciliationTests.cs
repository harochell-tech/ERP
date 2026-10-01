using System.Text.Json;
using Rochell.Reconciliation;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1b-06: PROFORMA-ASIG — proformas and allocations agree with deliveries and receipts, and block the AR close when they do not (E-FIS1b-11, E-FIS1b-01-12).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProformaReconciliationTests(PostgresFixture postgres)
{
    private static readonly string[] Codes = ["ACC-EVIDENCE", "AR-GL", "CONTRACT-ASSET", "PROFORMA-ASIG", "RECEIPT-APPL"];

    private static async Task<string> RunAsync(TestHarness h, Guid controller, string key)
    {
        var result = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, Codes), new RunReconciliationHandler())).ResultPayload).RootElement;
        return string.Join(',', result.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal));
    }

    [Trait("AcceptanceFis1b", "PRF-13")]
    [Fact]
    public async Task PRF13_a_month_with_proformas_allocations_and_an_invoice_from_a_proforma_reconciles()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var (order, line) = await ProformaTests.OrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 1000m, collectsItbis: true, "pf");
        var proformas = new List<Guid>();
        foreach (var (quantity, key) in new[] { (600m, "a"), (300m, "b"), (100m, "c") })
        {
            var (delivery, _) = await DeliveryTests.DispatchAsync(h, w.S, order, line, quantity, own: false, "pf-" + key);
            proformas.Add(await h.ScalarAsync<Guid>("SELECT proforma_id FROM sal.proforma WHERE delivery_id = @d", ("d", delivery)));
        }

        // PF-000001 collected and invoiced (e-CF 31, paid); PF-000002 partly collected; PF-000003 voided and left for normal invoicing.
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 45400.00m)).ResultRef;
        await h.RunAsync(
            new AllocateReceiptToProformas(h.CompanyId, w.Cobros, "al", receipt, 1, [new(proformas[0], 35400.00m), new(proformas[1], 10000.00m)]), new AllocateReceiptToProformasHandler());
        var invoice = (await h.RunAsync(new CreateInvoiceFromProformas(h.CompanyId, w.Billing, "pf-i", w.S.Customer, [proformas[0]]), new CreateInvoiceFromProformasHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, w.Billing, "pf-issue", invoice, 1), new IssueInvoiceHandler());
        await h.RunAsync(new VoidProforma(h.CompanyId, w.Billing, "pf-void", proformas[2], 1, "Entrega marcada por error"), new VoidProformaHandler());

        Assert.Equal("ACC-EVIDENCE:MATCHED,AR-GL:MATCHED,CONTRACT-ASSET:MATCHED,PROFORMA-ASIG:MATCHED,RECEIPT-APPL:MATCHED", await RunAsync(h, w.Controller, "recon"));
    }

    [Fact]
    public async Task A_proforma_or_a_receipt_that_disagrees_with_its_allocations_or_its_delivery_is_an_exception()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var (order, line) = await ProformaTests.OrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 300m, collectsItbis: false, "pf");
        var (first, _) = await DeliveryTests.DispatchAsync(h, w.S, order, line, 200m, own: false, "pf-a");
        var (second, secondLine) = await DeliveryTests.DispatchAsync(h, w.S, order, line, 100m, own: false, "pf-b");
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 5000.00m)).ResultRef;

        // Tampered by the owner role, as a defect would leave them: the allocated amounts without allocation rows, and a delivery of
        // an OPEN proforma invoiced outside it.
        await h.AdminRequireAsync($"UPDATE sal.proforma SET allocated_amount = 1000, version = version + 1 WHERE delivery_id = '{first}'");
        await h.AdminRequireAsync($"UPDATE fin.receipt SET allocated_amount = 1000, version = version + 1 WHERE receipt_id = '{receipt}'");
        await h.AdminRequireAsync($"UPDATE log.delivery_line SET qty_invoiced = qty_delivered WHERE delivery_line_id = '{secondLine}'");

        var statuses = await RunAsync(h, w.Controller, "recon");
        var findings = await h.ScalarAsync<string>(
            """
            SELECT string_agg(x.match_key || ':' || x.classification, ',' ORDER BY x.match_key, x.classification)
            FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id) WHERE r.recon_code = 'PROFORMA-ASIG'
            """);

        Assert.Contains("PROFORMA-ASIG:EXCEPTIONS", statuses, StringComparison.Ordinal);
        Assert.Equal("PF:PF-000001:PROFORMA_ALLOCATION_DIFFERENCE,PF:PF-000002:PROFORMA_UNBILLED_DIFFERENCE,REC:REC-000001:RECEIPT_ALLOCATION_DIFFERENCE", findings);
        Assert.Equal("AR-REC", await h.ScalarAsync<string>("SELECT component FROM rec.recon_blocking WHERE recon_code = 'PROFORMA-ASIG'"));
        Assert.Equal(second, await h.ScalarAsync<Guid>("SELECT delivery_id FROM sal.proforma WHERE proforma_no = 'PF-000002'"));
    }
}
