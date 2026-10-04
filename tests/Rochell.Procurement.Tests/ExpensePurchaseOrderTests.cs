using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.Queries;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// GAS1-05 (E-GAS-05-1…6, GAS-10, GAS-11): the expense purchase order — approved as any order, never received, billed by expense
/// invoices that carry its lines' category and tax type, closed once billed in full and reopened by a reversal.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExpensePurchaseOrderTests(PostgresFixture postgres)
{
    /// <summary>An APPROVED order of 10 maintenance services at 1,000.00 (ITBIS 18 %); returns the order and its line.</summary>
    private static async Task<(Guid Po, Guid Line)> ApprovedAsync(TestHarness h, World w, string key = "po", decimal quantity = 10m)
    {
        var p = w.S.Purchasing;
        var po = (await h.RunAsync(
            new CreateExpensePurchaseOrder(
                h.CompanyId, p.Buyer, key, p.PlantId, p.SupplierId, Today(h),
                [new ExpenseOrderLineInput("Mantenimiento preventivo de la bloquera", w.Categories["REPARACIONES"], w.Types["ITBIS_18"], quantity, 1000m)]),
            new CreateExpensePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, key + "-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Approver, key + "-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        return (po, await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po)));
    }

    private static async Task<Guid> BillAsync(TestHarness h, World w, string key, string ncf, Guid po, Guid line, decimal quantity, decimal price = 1000m)
        => (await h.RunAsync(
            new RegisterExpenseInvoice(
                h.CompanyId, w.Clerk, key, w.S.Purchasing.SupplierId, ncf, Today(h), Today(h).AddDays(30), w.Plant,
                [new ExpenseLineInput("Mantenimiento preventivo", w.Categories["REPARACIONES"], w.Types["ITBIS_18"], quantity, price, line)], PurchaseOrderId: po),
            new RegisterExpenseInvoiceHandler())).ResultRef;

    private static async Task<string> MatchAsync(TestHarness h, World w, Guid si, string key)
        => JsonDocument.Parse((await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, w.Clerk, key, si, 1), new MatchSupplierInvoiceHandler())).ResultPayload)
            .RootElement.GetProperty("status").GetString()!;

    private static Task<string?> OrderAsync(TestHarness h, Guid po)
        => h.ScalarAsync<string>(
            "SELECT o.status::text || ':' || l.qty_invoiced::numeric(18,0) FROM pur.purchase_order o JOIN pur.purchase_order_line l ON l.po_id = o.po_id WHERE o.po_id = @p", ("p", po));

    [Trait("AcceptanceGas1", "GAS-10")]
    [Fact]
    public async Task GAS10_an_expense_order_is_billed_up_to_what_it_ordered_closes_when_complete_and_reopens_with_a_reversal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (po, line) = await ApprovedAsync(h, w);

        var first = await BillAsync(h, w, "i1", "B0100000801", po, line, 6m);
        var firstMatch = await MatchAsync(h, w, first, "m1");
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "p1", first, 2), new PostSupplierInvoiceHandler());
        var afterFirst = await OrderAsync(h, po);

        // 5 more exceed the 4 still to bill: never approvable.
        var second = await BillAsync(h, w, "i2", "B0100000802", po, line, 5m);
        var secondMatch = await MatchAsync(h, w, second, "m2");
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ApproveMatchException(h.CompanyId, w.Controller, "a2", second, 2, "Se pidió más"), new ApproveMatchExceptionHandler()));
        await h.RunAsync(new VoidSupplierInvoice(h.CompanyId, w.Clerk, "v2", second, 2, "Excede la orden"), new VoidSupplierInvoiceHandler());

        // The last 4: the order is complete and CLOSED; reversing the first invoice reopens it.
        var third = await BillAsync(h, w, "i3", "B0100000803", po, line, 4m);
        await MatchAsync(h, w, third, "m3");
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "p3", third, 2), new PostSupplierInvoiceHandler());
        var complete = await OrderAsync(h, po);
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, w.Controller, "x1", first, 3, "Duplicada"), new ReverseSupplierInvoiceHandler());

        Assert.Equal(("MATCHED", "APPROVED:6"), (firstMatch, afterFirst));
        Assert.Equal((SupplierInvoiceStatus.MatchException, ProcurementErrors.QtyExceptionNotApprovable), (secondMatch, refused.Code));
        Assert.Equal("CLOSED:10", complete);
        Assert.Equal("APPROVED:4", await OrderAsync(h, po));
        Assert.Equal(
            "DRAFT>PENDING_APPROVAL,PENDING_APPROVAL>APPROVED,APPROVED>CLOSED,CLOSED>APPROVED",
            await h.ScalarAsync<string>($"SELECT string_agg(from_state || '>' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{po}' AND from_state IS NOT NULL"));
        // 4,000.00 + 18 % still payable (the 6,000.00 + ITBIS was reversed).
        Assert.Equal("720.00|0.00|0.00|0.00|0.00|-4720.00|63700:4000.00|7080.00/0.00,4720.00/4720.00", await BooksAsync(h));
    }

    [Fact]
    public async Task A_price_outside_the_tolerance_is_an_exception_the_Controller_approves_and_no_approval_amount_applies()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (po, line) = await ApprovedAsync(h, w, quantity: 30m);

        // 30 at 1,000.00 = 35,400.00 with ITBIS, above the 25,000.00 approval amount: the order already approved it.
        var exact = await BillAsync(h, w, "i1", "B0100000801", po, line, 25m);
        var dearer = await BillAsync(h, w, "i2", "B0100000802", po, line, 5m, 1100m);

        Assert.Equal("MATCHED", await MatchAsync(h, w, exact, "m1"));
        Assert.Equal(SupplierInvoiceStatus.MatchException, await MatchAsync(h, w, dearer, "m2"));
        await h.RunAsync(new ApproveMatchException(h.CompanyId, w.Controller, "a2", dearer, 2, "Ajuste de precio aceptado"), new ApproveMatchExceptionHandler());
        Assert.Equal("MATCHED", await h.ScalarAsync<string>("SELECT document_status::text FROM pur.supplier_invoice WHERE si_id = @s", ("s", dearer)));
    }

    [Trait("AcceptanceGas1", "GAS-11")]
    [Fact]
    public async Task GAS11_an_expense_order_is_not_received_and_is_closed_or_cancelled_only_as_its_rules_allow()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var p = w.S.Purchasing;
        var (po, line) = await ApprovedAsync(h, w);
        var toReceive = JsonDocument.Parse(await h.QueryAsync(new ListPurchaseOrdersToReceive(h.CompanyId, p.Buyer), new ListPurchaseOrdersToReceiveHandler())).RootElement.GetProperty("items");
        var receive = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, w.S.Receiving.Storekeeper, "gr", p.PlantId, po, w.S.Receiving.LocationA, h.Clock.UtcNow.AddMinutes(-1), [new(line, 1m)]),
            new PostGoodsReceiptHandler()));
        await BillAsync(h, w, "i1", "B0100000801", po, line, 2m);
        var cancel = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CancelPurchaseOrder(h.CompanyId, p.Buyer, "c", p.PlantId, po, 3, "Ya no"), new CancelPurchaseOrderHandler()));
        await h.RunAsync(new CloseExpensePurchaseOrder(h.CompanyId, p.Buyer, "close", p.PlantId, po, 3, "El proveedor no hará el resto"), new CloseExpensePurchaseOrderHandler());
        var billClosed = await Assert.ThrowsAsync<DomainException>(() => BillAsync(h, w, "i2", "B0100000802", po, line, 1m));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetPurchaseOrder(h.CompanyId, p.Buyer, po, p.PlantId), new GetPurchaseOrderHandler())).RootElement;

        Assert.DoesNotContain(toReceive.EnumerateArray(), o => o.GetProperty("purchaseOrderId").GetGuid() == po);
        Assert.Equal((ProcurementErrors.NotReceivable, ProcurementErrors.AlreadyInvoiced, ProcurementErrors.InvalidState), (receive.Code, cancel.Code, billClosed.Code));
        Assert.Equal(
            "EXPENSE|CLOSED|Mantenimiento preventivo de la bloquera|Reparaciones|ITBIS_18|10.000000",
            $"{detail.GetProperty("docClass").GetString()}|{detail.GetProperty("status").GetString()}|" +
            string.Join('|', new[] { "description", "expenseCategoryName", "taxTypeCode", "openQuantity" }.Select(n => detail.GetProperty("lines")[0].GetProperty(n).GetString())));
    }

    [Fact]
    public async Task An_invoice_cites_one_approved_order_of_its_supplier_and_carries_its_lines_category_and_tax_type()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (po, line) = await ApprovedAsync(h, w);
        Task<CommandResult> Register(string key, ExpenseLineInput input, Guid? order)
            => h.RunAsync(
                new RegisterExpenseInvoice(h.CompanyId, w.Clerk, key, w.S.Purchasing.SupplierId, "B01000009" + key[^2..], Today(h), Today(h), w.Plant, [input], PurchaseOrderId: order),
                new RegisterExpenseInvoiceHandler());

        var otherCategory = await Assert.ThrowsAsync<DomainException>(() => Register(
            "x01", new ExpenseLineInput("Mantenimiento", w.Categories["TELEFONO"], w.Types["ITBIS_18"], 1m, 1000m, line), po));
        var lineWithoutOrder = await Assert.ThrowsAsync<DomainException>(() => Register(
            "x02", new ExpenseLineInput("Mantenimiento", w.Categories["REPARACIONES"], w.Types["ITBIS_18"], 1m, 1000m, line), null));
        var missingLine = await Assert.ThrowsAsync<DomainException>(() => Register(
            "x03", new ExpenseLineInput("Mantenimiento", w.Categories["REPARACIONES"], w.Types["ITBIS_18"], 1m, 1000m), po));
        var notAnOrder = await Assert.ThrowsAsync<DomainException>(() => Register(
            "x04", new ExpenseLineInput("Mantenimiento", w.Categories["REPARACIONES"], w.Types["ITBIS_18"], 1m, 1000m, line), w.S.PurchaseOrderId));

        Assert.Equal(
            (ExpenseErrors.CategoryInvalid, ProcurementErrors.LineNotFound, ProcurementErrors.DuplicateLine, ProcurementErrors.NotFound),
            (otherCategory.Code, lineWithoutOrder.Code, missingLine.Code, notAnOrder.Code));
    }

    [Fact]
    public async Task The_preview_prices_an_expense_order_with_the_taxes_of_its_lines_types()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        var preview = JsonDocument.Parse(await h.QueryAsync(
            new PreviewExpensePurchaseOrder(
                h.CompanyId, w.S.Purchasing.Buyer, Today(h),
                [
                    new ExpenseOrderLineInput("Teléfono", w.Categories["TELEFONO"], w.Types["TELECOM"], 1m, 5000m),
                    new ExpenseOrderLineInput("Gasoil", w.Categories["COMBUSTIBLE"], w.Types["EXENTO"], 30m, 100m),
                ]),
            new PreviewExpensePurchaseOrderHandler())).RootElement;

        // 5,000.00 telephone: 900 + 500 + 100 = 1,500.00; 3,000.00 fuel: nothing.
        Assert.Equal(
            "5000.00:1500.00,3000.00:0.00|8000.00|1500.00|9500.00",
            string.Join(',', preview.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("netAmount").GetString()}:{l.GetProperty("taxes").GetString()}"))
            + $"|{preview.GetProperty("netTotal").GetString()}|{preview.GetProperty("taxTotal").GetString()}|{preview.GetProperty("total").GetString()}");
    }
}
