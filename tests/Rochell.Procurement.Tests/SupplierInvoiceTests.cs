using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>§11.4 register / match / approve exception / void: SI-01…SI-05 (non-posting parts), E-PR13-1/2/4/5.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierInvoiceTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<Guid> Register(TestHarness h, TestInvoicing s, string key, decimal qty, decimal price, string ncf = "B0100000001", Guid? session = null, string kind = SupplierInvoiceLineKinds.InventoryPo)
        => (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, session ?? s.Clerk, key, s.Purchasing.SupplierId, ncf, Today(h), Today(h).AddDays(30), [new(s.PoLineId, qty, price, kind)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;

    private static async Task<(string Status, bool QtyExceeds)> Match(TestHarness h, TestInvoicing s, Guid si, long version, string key)
    {
        var result = JsonDocument.Parse((await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, key, si, version), new MatchSupplierInvoiceHandler())).ResultPayload).RootElement;
        return (result.GetProperty("status").GetString()!, result.GetProperty("qtyExceeds").GetBoolean());
    }

    private static Task<string?> State(TestHarness h, Guid si)
        => h.ScalarAsync<string>("SELECT document_status::text || '|' || accounting_status::text || '|' || version FROM pur.supplier_invoice WHERE si_id = @s", ("s", si));

    [Fact]
    public async Task Register_and_match_within_tolerance_including_partial_invoicing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();

        var si = await Register(h, s, "reg", 4m, 1500m);
        var registered = await State(h, si);
        var (status, exceeds) = await Match(h, s, si, 1, "match");

        Assert.Equal("DRAFT|NOT_POSTED|1", registered);
        Assert.Equal(("MATCHED", false), (status, exceeds));
        Assert.Equal("6000.0000|2.000000", await h.ScalarAsync<string>(
            "SELECT (SELECT total_amount FROM pur.supplier_invoice) || '|' || (SELECT qty_available_to_invoice - (SELECT qty FROM pur.supplier_invoice_line) FROM pur.match_result)"));
    }

    [Theory]
    [InlineData(1512, 6, "MATCHED")]          // |12| ≤ 1 % × 1 500 = 15
    [InlineData(1520, 0.2, "MATCHED")]        // price 20 > 15, but amount 0.2 × 20 = 4 ≤ 5
    [InlineData(1520, 6, "MATCH_EXCEPTION")]  // price 20 > 15 and amount 120 > 5
    [InlineData(1480, 6, "MATCH_EXCEPTION")]  // cheaper beyond both tolerances is an exception too
    public async Task Price_tolerance_is_a_percentage_or_an_absolute_amount(double price, double qty, string expected)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var si = await Register(h, s, "reg", (decimal)qty, (decimal)price);

        var (status, exceeds) = await Match(h, s, si, 1, "match");

        Assert.Equal(expected, status);
        Assert.False(exceeds);
    }

    [Fact]
    public async Task Price_exceptions_are_approved_by_a_re_authenticated_controller()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await h.CreateInvoicingSetupAsync();
        var si = await Register(h, s, "reg", 6m, 1600m);
        await Match(h, s, si, 1, "match");

        var clerk = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveMatchException(h.CompanyId, s.Clerk, "ok-0", si, 2, "Aumento de precio pactado"), new ApproveMatchExceptionHandler()));
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveMatchException(h.CompanyId, s.Purchasing.Controller, "ok-1", si, 2, "Aumento de precio pactado"), new ApproveMatchExceptionHandler()));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await h.RunAsync(new ApproveMatchException(h.CompanyId, controller, "ok-2", si, 2, "Aumento de precio pactado"), new ApproveMatchExceptionHandler());

        Assert.Equal(AuthorizationErrors.NotAuthorized, clerk.Code);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
        Assert.Equal("MATCHED|NOT_POSTED|3", await State(h, si));
        Assert.True(await h.ScalarAsync<bool>("SELECT exception_approved_by IS NOT NULL AND exception_approved_by <> created_by FROM pur.supplier_invoice"));
    }

    [Fact]
    public async Task SI02_SI03_billing_more_than_received_is_not_approvable_until_the_receipt_is_corrected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableCorrectionsAsync();
        var si = await Register(h, s, "reg", 7m, 1500m);

        var first = await Match(h, s, si, 1, "match-1");
        var approve = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ApproveMatchException(h.CompanyId, s.Purchasing.Controller, "ok", si, 2, "x"), new ApproveMatchExceptionHandler()));
        var rc = await h.RunAsync(
            new CreateReceiptCorrection(h.CompanyId, s.Receiving.Storekeeper, "rc", s.Purchasing.PlantId, s.GoodsReceiptId, s.GoodsReceiptLineId, 1m, "Ticket corregido", "BAS-9"),
            new CreateReceiptCorrectionHandler());
        await h.RunAsync(new ApproveReceiptCorrection(h.CompanyId, s.Purchasing.Controller, "rc-ok", rc.ResultRef), new ApproveReceiptCorrectionHandler());
        var second = await Match(h, s, si, 2, "match-2");

        Assert.Equal(("MATCH_EXCEPTION", true), first);
        Assert.Equal(ProcurementErrors.QtyExceptionNotApprovable, approve.Code);
        Assert.Equal(("MATCHED", false), second);
        Assert.Equal("MATCHED|NOT_POSTED|3", await State(h, si));
    }

    [Fact]
    public async Task SI04_SI05_a_voided_invoice_frees_its_fiscal_number_and_an_active_one_does_not()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var first = await Register(h, s, "reg-1", 6m, 1500m, ncf: "E310000000123");

        var duplicate = await Assert.ThrowsAsync<DomainException>(() => Register(h, s, "reg-2", 6m, 1500m, ncf: "E310000000123"));
        await h.RunAsync(new VoidSupplierInvoice(h.CompanyId, s.Clerk, "void", first, 1, "Digitada con precio equivocado"), new VoidSupplierInvoiceHandler());
        var again = await Register(h, s, "reg-3", 6m, 1500m, ncf: "E310000000123");
        var voidTwice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidSupplierInvoice(h.CompanyId, s.Clerk, "void-2", first, 2, "x"), new VoidSupplierInvoiceHandler()));
        var matchVoided = await Assert.ThrowsAsync<DomainException>(() => Match(h, s, first, 2, "m"));

        Assert.Equal(ProcurementErrors.FiscalNumberUsed, duplicate.Code);
        Assert.Equal("VOIDED|NOT_POSTED|2", await State(h, first));
        Assert.Equal("DRAFT|NOT_POSTED|1", await State(h, again));
        Assert.Equal(ProcurementErrors.InvalidState, voidTwice.Code);
        Assert.Equal(ProcurementErrors.InvalidState, matchVoided.Code);
    }

    public static TheoryData<string, string> InvalidRegistrations() => new()
    {
        { "ncf:B123", ProcurementErrors.FiscalNumberInvalid },
        { "ncf:A0100000001", ProcurementErrors.FiscalNumberInvalid },
        { "ncf:E12345678901", ProcurementErrors.FiscalNumberInvalid },
        { "kind:EXPENSE", ProcurementErrors.LineKindNotSupported },
        { "date:future", ProcurementErrors.DateInvalid },
        { "date:due-before", ProcurementErrors.DateInvalid },
        { "qty:0", ProcurementErrors.QuantityInvalid },
        { "price:0", ProcurementErrors.PriceInvalid },
        { "line:other-supplier", ProcurementErrors.PoLineNotInvoiceable },
        { "line:unknown", ProcurementErrors.LineNotFound },
    };

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public async Task SI01_and_other_invalid_registrations_are_rejected(string variant, string expectedCode)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var (what, value) = (variant.Split(':')[0], variant.Split(':')[1]);
        var doc = what == "date" && value == "future" ? Today(h).AddDays(1) : Today(h);
        var due = what == "date" && value == "due-before" ? doc.AddDays(-1) : doc.AddDays(30);
        var poLine = s.PoLineId;
        if (what == "line")
        {
            poLine = value == "unknown" ? Guid.CreateVersion7() : await OtherSupplierPoLineAsync(h, s);
        }

        var line = new SupplierInvoiceLineInput(poLine, what == "qty" ? 0m : 1m, what == "price" ? 0m : 1500m, what == "kind" ? value : SupplierInvoiceLineKinds.InventoryPo);
        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "reg", s.Purchasing.SupplierId, what == "ncf" ? value : "B0100000001", doc, due, [line]),
            new RegisterSupplierInvoiceHandler()));

        Assert.Equal(expectedCode, ex.Code);
        Assert.Equal(0L, await h.CountAsync("pur.supplier_invoice"));
    }

    [Fact]
    public async Task Storekeepers_cannot_register_invoices()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Register(h, s, "reg", 6m, 1500m, session: s.Receiving.Storekeeper));

        Assert.Equal(AuthorizationErrors.NotAuthorized, ex.Code);
    }

    private static async Task<Guid> OtherSupplierPoLineAsync(TestHarness h, TestInvoicing s)
    {
        var p = s.Purchasing;
        var other = await h.CreateActiveSupplierAsync("131000022", "Otro proveedor, S.R.L.");
        var po = (await h.RunAsync(new Procurement.PurchaseOrders.CreatePurchaseOrder(h.CompanyId, p.Buyer, "o-c", p.PlantId, other, Today(h), [new(p.Sand, "t", 5m, 1500m)]), new Procurement.PurchaseOrders.CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new Procurement.PurchaseOrders.SubmitPurchaseOrder(h.CompanyId, p.Buyer, "o-s", p.PlantId, po, 1), new Procurement.PurchaseOrders.SubmitPurchaseOrderHandler());
        await h.RunAsync(new Procurement.PurchaseOrders.ApprovePurchaseOrder(h.CompanyId, p.Controller, "o-a", p.PlantId, po, 2), new Procurement.PurchaseOrders.ApprovePurchaseOrderHandler());
        return await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
    }
}
