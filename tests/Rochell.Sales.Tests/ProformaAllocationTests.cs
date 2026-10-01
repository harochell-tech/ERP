using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;
using Rochell.Sales.Receipts;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1b-03: receipts allocated to proformas, with no journal (option A); credit, aging and statement (E-FIS1b-3/4, E-FIS1b-01-4/5).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProformaAllocationTests(PostgresFixture postgres)
{
    /// <summary>The receipts world (an invoice of 100 blocks, 5,900.00 open) plus a marked order with one proforma of <paramref name="blocks"/> blocks.</summary>
    internal static async Task<(ReceiptTests.World W, Guid Proforma, Guid Delivery)> WorldAsync(TestHarness h, bool collectsItbis, decimal blocks = 600m)
    {
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var (order, line) = await ProformaTests.OrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, blocks, collectsItbis, "pf");
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, w.S, order, line, blocks, own: false, "pf-d");
        return (w, await h.ScalarAsync<Guid>("SELECT proforma_id FROM sal.proforma WHERE delivery_id = @d", ("d", delivery)), delivery);
    }

    private static Task<CommandResult> Allocate(TestHarness h, ReceiptTests.World w, string key, Guid receipt, long version, Guid proforma, decimal amount, Guid? session = null)
        => h.RunAsync(new AllocateReceiptToProformas(h.CompanyId, session ?? w.Cobros, key, receipt, version, [new(proforma, amount)]), new AllocateReceiptToProformasHandler());

    private static Task<string?> State(TestHarness h, Guid proforma, Guid receipt)
        => h.ScalarAsync<string>(
            """
            SELECT concat_ws('|', f.status, f.allocated_amount::numeric(19,2), r.application_status, r.unapplied_amount::numeric(19,2), r.allocated_amount::numeric(19,2))
            FROM sal.proforma f, fin.receipt r WHERE f.proforma_id = @f AND r.receipt_id = @r
            """,
            ("f", proforma),
            ("r", receipt));

    private static async Task<JsonElement> ExposureAsync(TestHarness h, ReceiptTests.World w)
        => JsonDocument.Parse(await h.QueryAsync(new GetCustomerExposure(h.CompanyId, w.S.Seller, w.S.Customer), new GetCustomerExposureHandler())).RootElement;

    [Trait("AcceptanceFis1b", "PRF-03")]
    [Fact]
    public async Task PRF03_a_receipt_allocated_to_a_proforma_settles_it_and_frees_credit_without_a_journal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (w, proforma, _) = await WorldAsync(h, collectsItbis: true); // 600 × 50.00 = 30,000.00 + ITBIS 5,400.00 = 35,400.00
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 40000.00m)).ResultRef;
        var before = await ExposureAsync(h, w);
        var journals = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");

        var seller = await Assert.ThrowsAsync<DomainException>(() => Allocate(h, w, "s", receipt, 1, proforma, 35400.00m, w.S.Seller));
        var tooMuch = await Assert.ThrowsAsync<DomainException>(() => Allocate(h, w, "x", receipt, 1, proforma, 35400.01m));
        var result = JsonDocument.Parse((await Allocate(h, w, "a", receipt, 1, proforma, 35400.00m)).ResultPayload).RootElement;
        var after = await ExposureAsync(h, w);
        // What is allocated waits for the proforma's invoice: only the other 4,600.00 can be applied to an invoice now.
        var applyAllocated = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ApplyReceipt(h.CompanyId, w.Cobros, "ap", receipt, 2, [new(w.Invoice, 5900.00m)]), new ApplyReceiptHandler()));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetProforma(h.CompanyId, w.S.Seller, proforma), new GetProformaHandler())).RootElement;
        var seen = JsonDocument.Parse(await h.QueryAsync(new GetReceipt(h.CompanyId, w.S.Seller, receipt), new GetReceiptHandler())).RootElement;

        Assert.Equal((AuthorizationErrors.NotAuthorized, AllocationErrors.ExceedsBalance, ReceiptErrors.ExceedsUnapplied), (seller.Code, tooMuch.Code, applyAllocated.Code));
        Assert.Equal(("35400.00", "4600.00"), (result.GetProperty("allocated").GetString(), result.GetProperty("available").GetString()));
        Assert.Equal("OPEN|35400.00|UNAPPLIED|40000.00|35400.00", await State(h, proforma, receipt));
        // Option A: no journal; the receipt stays whole in UNAPPLIED_RECEIPTS and the unbilled receivable is untouched.
        Assert.Equal(journals, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal"));
        Assert.Equal("UNAPPLIED_RECEIPTS=-40000.00|CONTRACT_ASSET=30000.00", await DeliveryTests.Balances(h, w.S, "UNAPPLIED_RECEIPTS", "CONTRACT_ASSET"));
        // E-FIS1b-01-5: the 30,000.00 delivered and not invoiced no longer use credit.
        Assert.Equal(("30000.00", "0.00"), (before.GetProperty("deliveredUninvoiced").GetString(), after.GetProperty("deliveredUninvoiced").GetString()));
        Assert.Equal(
            ("0.00", "5400.00", "REC-000001", "35400.00"),
            (detail.GetProperty("header").GetProperty("balance").GetString(), detail.GetProperty("header").GetProperty("deposit").GetString(),
             detail.GetProperty("collections")[0].GetProperty("receiptNo").GetString(), detail.GetProperty("collections")[0].GetProperty("amount").GetString()));
        Assert.Equal(
            ("35400.00", "4600.00", true),
            (seen.GetProperty("header").GetProperty("allocated").GetString(), seen.GetProperty("header").GetProperty("available").GetString(),
             seen.GetProperty("allocations")[0].GetProperty("live").GetBoolean()));
    }

    [Trait("AcceptanceFis1b", "PRF-04")]
    [Fact]
    public async Task PRF04_a_proforma_that_collects_without_ITBIS_takes_its_net_and_an_allocation_is_released_whole()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (w, proforma, _) = await WorldAsync(h, collectsItbis: false); // net 30,000.00
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 31000.00m)).ResultRef;

        var aboveNet = await Assert.ThrowsAsync<DomainException>(() => Allocate(h, w, "x", receipt, 1, proforma, 30000.01m));
        var allocation = JsonDocument.Parse((await Allocate(h, w, "a", receipt, 1, proforma, 30000.00m)).ResultPayload).RootElement.GetProperty("allocationEventId").GetGuid();
        var allocated = await State(h, proforma, receipt);
        var reversed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseReceipt(h.CompanyId, w.Controller, "rev", receipt, 2, "Error de registro"), new ReverseReceiptHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseProformaAllocation(h.CompanyId, w.Cobros, "n", receipt, allocation, " "), new ReleaseProformaAllocationHandler()));
        await h.RunAsync(new ReleaseProformaAllocation(h.CompanyId, w.Cobros, "rel", receipt, allocation, "Se asignó a la proforma equivocada"), new ReleaseProformaAllocationHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseProformaAllocation(h.CompanyId, w.Cobros, "rel2", receipt, allocation, "Otra vez"), new ReleaseProformaAllocationHandler()));

        Assert.Equal(
            (AllocationErrors.ExceedsBalance, ReceiptErrors.NotReversible, ReceiptErrors.ReasonRequired, AllocationErrors.NotFound),
            (aboveNet.Code, reversed.Code, noReason.Code, twice.Code));
        Assert.Equal("OPEN|30000.00|UNAPPLIED|31000.00|30000.00", allocated);
        Assert.Equal("OPEN|0.00|UNAPPLIED|31000.00|0.00", await State(h, proforma, receipt));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.proforma_allocation WHERE receipt_id = @r", ("r", receipt)));
    }

    [Trait("AcceptanceFis1b", "PRF-10")]
    [Fact]
    public async Task PRF10_a_bounced_cheque_releases_its_allocations()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (w, proforma, _) = await WorldAsync(h, collectsItbis: true);
        var receipt = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cobros, "chq", w.S.Customer, "CHEQUE", 35400.00m, ChequeBank: "Banco Popular", ChequeNo: "000123", ChequeDate: ReceiptTests.Today(h)),
            new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new DepositReceipts(h.CompanyId, w.Cobros, "dep", w.Bank, [receipt]), new DepositReceiptsHandler());
        await Allocate(h, w, "a", receipt, 2, proforma, 35400.00m);

        await h.RunAsync(new MarkReceiptBounced(h.CompanyId, w.Treasurer, "b", receipt, 3, "Fondos insuficientes"), new MarkReceiptBouncedHandler());

        Assert.Equal("OPEN|0.00|UNAPPLIED|35400.00|0.00", await State(h, proforma, receipt));
        Assert.Equal("BOUNCED", await h.ScalarAsync<string>("SELECT status FROM fin.receipt WHERE receipt_id = @r", ("r", receipt)));
        Assert.Equal("30000.00", (await ExposureAsync(h, w)).GetProperty("deliveredUninvoiced").GetString()); // nothing collected: the credit is used again
    }

    [Trait("AcceptanceFis1b", "PRF-05")]
    [Fact]
    public async Task PRF05_an_overdue_proforma_with_a_balance_counts_as_overdue_days()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var (w, proforma, _) = await WorldAsync(h, collectsItbis: false, 100m); // net 5,000.00, due in 30 days
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 5900.00m + 5000.00m)).ResultRef;
        await h.RunAsync(new ApplyReceipt(h.CompanyId, w.Cobros, "ap", receipt, 1, [new(w.Invoice, 5900.00m)]), new ApplyReceiptHandler()); // the invoice is paid: nothing else is overdue

        clock.Advance(TimeSpan.FromDays(40));
        var seller = await h.SessionWithRolesAsync("VENDEDOR"); // the sessions of 40 days ago have expired
        var cobros = await h.SessionWithRolesAsync("COBROS");
        async Task<int> OverdueAsync()
            => JsonDocument.Parse(await h.QueryAsync(new GetCustomerExposure(h.CompanyId, seller, w.S.Customer), new GetCustomerExposureHandler())).RootElement.GetProperty("overdueDays").GetInt32();
        var overdue = await OverdueAsync();
        await Allocate(h, w, "a", receipt, 2, proforma, 5000.00m, cobros);
        var settled = await OverdueAsync();

        Assert.Equal((10, 0), (overdue, settled));
    }

    [Fact]
    public async Task The_aging_and_the_statement_show_open_proformas_apart_and_leave_allocated_money_out_of_the_advances()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (w, proforma, _) = await WorldAsync(h, collectsItbis: true); // 35,400.00
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 40000.00m)).ResultRef;
        await Allocate(h, w, "a", receipt, 1, proforma, 32400.00m); // 2,400.00 of ITBIS advanced; 3,000.00 still to collect

        var agingJson = await h.QueryAsync(new GetArAging(h.CompanyId, w.S.Seller), new GetArAgingHandler());
        var customer = JsonDocument.Parse(agingJson).RootElement.GetProperty("customers")[0];
        var statementJson = await h.QueryAsync(new GetCustomerStatement(h.CompanyId, w.S.Seller, w.S.Customer, ReceiptTests.Today(h).AddDays(-1), ReceiptTests.Today(h)), new GetCustomerStatementHandler());
        var statement = JsonDocument.Parse(statementJson).RootElement;

        // Invoices: 5,900.00 open; advances: 40,000.00 − 32,400.00 allocated = 7,600.00; proformas apart: 3,000.00 to collect, 2,400.00 of deposit.
        Assert.Equal(
            ("5900.00", "7600.00", "-1700.00", "3000.00", "2400.00", "PF-000001", "CURRENT"),
            (customer.GetProperty("total").GetString(), customer.GetProperty("unapplied").GetString(), customer.GetProperty("net").GetString(), customer.GetProperty("proformas").GetString(),
             customer.GetProperty("deposits").GetString(), customer.GetProperty("proformaDocuments")[0].GetProperty("proformaNo").GetString(),
             customer.GetProperty("proformaDocuments")[0].GetProperty("bucket").GetString()));
        Assert.Contains("PF-000001,Proforma (sin e-CF)", ArCsv.Aging(agingJson), StringComparison.Ordinal);
        Assert.Contains("Total proformas pendientes de e-CF,,,,,,,3000.00", ArCsv.Aging(agingJson), StringComparison.Ordinal);
        Assert.Equal(
            ("3000.00", "PF-000001", "35400.00", "32400.00", "2400.00"),
            (statement.GetProperty("proformaBalance").GetString(), statement.GetProperty("openProformas")[0].GetProperty("proformaNo").GetString(),
             statement.GetProperty("openProformas")[0].GetProperty("total").GetString(), statement.GetProperty("openProformas")[0].GetProperty("allocated").GetString(),
             statement.GetProperty("openProformas")[0].GetProperty("deposit").GetString()));
        Assert.Contains("PROFORMA_SIN_ECF,PF-000001,35400.00,32400.00,3000.00", ArCsv.Statement(statementJson), StringComparison.Ordinal);
    }
}
