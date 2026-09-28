using System.Text.Json;
using Rochell.Platform.Queries;
using Rochell.Sales.Queries;
using Rochell.Sales.Receipts;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-09: AR aging, the customer's statement of account and the list filters (E-VS3-09-1…6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ArQueryTests(PostgresFixture postgres)
{
    /// <summary>FA-000001 (59,000.00, due in 30 days), an ISR withholding of 1,000.00, a transfer of 20,000.00 applied and an advance of 5,000.00.</summary>
    private static async Task<ReceiptTests.World> WorldAsync(TestHarness h)
    {
        var w = await ReceiptTests.WorldAsync(h);
        await h.RunAsync(
            new RecordCustomerWithholding(h.CompanyId, w.Cobros, "isr", w.Invoice, "ISR", 1000.00m, ReceiptTests.Today(h), "ISR-1", "isr-1.pdf", DeliveryTests.Hash),
            new RecordCustomerWithholdingHandler());
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 20000.00m)).ResultRef;
        await h.RunAsync(new ApplyReceipt(h.CompanyId, w.Cobros, "a", receipt, 1, [new(w.Invoice, 20000.00m)]), new ApplyReceiptHandler());
        await ReceiptTests.Transfer(h, w, "advance", 5000.00m);
        return w;
    }

    private static async Task<JsonElement> AgingAsync(TestHarness h, ReceiptTests.World w, DateOnly asOf)
        => JsonDocument.Parse(await h.QueryAsync(new GetArAging(h.CompanyId, w.Cobros, asOf), new GetArAgingHandler())).RootElement;

    [Fact]
    public async Task The_aging_moves_the_open_invoice_through_the_CREDIT_buckets_and_shows_the_advance_apart()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var today = ReceiptTests.Today(h);

        var now = await AgingAsync(h, w, today);
        var late = await AgingAsync(h, w, today.AddDays(45));   // 15 days overdue
        var older = await AgingAsync(h, w, today.AddDays(95));  // 65 days overdue

        var customer = Assert.Single(now.GetProperty("customers").EnumerateArray());
        Assert.Equal("38000.00|38000.00|5000.00|33000.00", $"{customer.GetProperty("current").GetString()}|{customer.GetProperty("total").GetString()}|{customer.GetProperty("unapplied").GetString()}|{customer.GetProperty("net").GetString()}");
        Assert.Equal("BUCKET_1:15", Doc(late));
        Assert.Equal("BUCKET_3:65", Doc(older));
        Assert.Equal("33000.00", older.GetProperty("net").GetString());
        var csv = ArCsv.Aging(now.GetRawText());
        Assert.Contains("Constructora Uno,FA-000001,,", csv, StringComparison.Ordinal);
        Assert.Contains("Constructora Uno,A favor (cobros no aplicados),,,,,,-5000.00", csv, StringComparison.Ordinal);
        Assert.EndsWith("Total,,,,,,,33000.00\r\n", csv, StringComparison.Ordinal);

        static string Doc(JsonElement aging)
        {
            var d = aging.GetProperty("customers")[0].GetProperty("documents")[0];
            return $"{d.GetProperty("bucket").GetString()}:{d.GetProperty("daysOverdue").GetInt32()}";
        }
    }

    [Fact]
    public async Task Without_the_aging_buckets_the_aging_refuses_instead_of_inventing_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h);

        // The fixture's CREDIT policy starts on 2020-01-01: before it there are no buckets.
        var missing = await Assert.ThrowsAsync<Platform.Commands.DomainException>(() => AgingAsync(h, w, new DateOnly(2019, 12, 31)));

        Assert.Equal(ArQueryErrors.PolicyMissing, missing.Code);
    }

    [Fact]
    public async Task The_statement_follows_the_customers_ledger_and_leaves_the_application_out()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var today = ReceiptTests.Today(h);

        var statement = JsonDocument.Parse(await h.QueryAsync(new GetCustomerStatement(h.CompanyId, w.Cobros, w.S.Customer, today.AddDays(-5), today), new GetCustomerStatementHandler())).RootElement;
        var tooLong = await Assert.ThrowsAsync<Platform.Commands.DomainException>(() => h.QueryAsync(
            new GetCustomerStatement(h.CompanyId, w.Cobros, w.S.Customer, today.AddDays(-366), today), new GetCustomerStatementHandler()));

        // 59,000.00 − 1,000.00 − 20,000.00 − 5,000.00 = 33,000.00 = open 38,000.00 − the 5,000.00 advance; the 20,000.00 application nets to zero.
        Assert.Equal("FACTURA:FA-000001:59000.00:0.00:59000.00|RETENCION:FA-000001:0.00:1000.00:58000.00|COBRO:REC-000001:0.00:20000.00:38000.00|COBRO:REC-000002:0.00:5000.00:33000.00",
            string.Join('|', statement.GetProperty("entries").EnumerateArray().Select(e =>
                $"{e.GetProperty("kind").GetString()}:{e.GetProperty("documentNo").GetString()}:{e.GetProperty("debit").GetString()}:{e.GetProperty("credit").GetString()}:{e.GetProperty("balance").GetString()}")));
        Assert.Equal("0.00|33000.00|131925332", $"{statement.GetProperty("opening").GetString()}|{statement.GetProperty("closing").GetString()}|{statement.GetProperty("rnc").GetString()}");
        Assert.Equal(QueryErrors.InvalidParameter, tooLong.Code);
        var csv = ArCsv.Statement(statement.GetRawText());
        Assert.StartsWith("Fecha,Tipo,Documento,Débito,Crédito,Saldo\r\n", csv, StringComparison.Ordinal);
        Assert.Contains(",Saldo final,,59000.00,26000.00,33000.00\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orders_and_deliveries_filter_by_customer_and_date()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h);
        var today = ReceiptTests.Today(h);

        async Task<int> Count<TQuery>(TQuery query, IQueryHandler<TQuery> handler)
            where TQuery : IQuery
            => JsonDocument.Parse(await h.QueryAsync(query, handler)).RootElement.GetProperty("items").GetArrayLength();

        Assert.Equal(1, await Count(new ListSalesOrders(h.CompanyId, w.Cobros, From: today, To: today), new ListSalesOrdersHandler()));
        Assert.Equal(0, await Count(new ListSalesOrders(h.CompanyId, w.Cobros, From: today.AddDays(1)), new ListSalesOrdersHandler()));
        Assert.Equal(1, await Count(new ListDeliveries(h.CompanyId, w.Cobros, PartyId: w.S.Customer, From: today, To: today), new ListDeliveriesHandler()));
        Assert.Equal(0, await Count(new ListDeliveries(h.CompanyId, w.Cobros, To: today.AddDays(-1)), new ListDeliveriesHandler()));
        Assert.Equal(0, await Count(new ListDeliveries(h.CompanyId, w.Cobros, PartyId: Guid.CreateVersion7()), new ListDeliveriesHandler()));
    }
}
