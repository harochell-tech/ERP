using Rochell.Platform.Data;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>Goods receipt guarantees in the database: K-25, ADR-027, append-only lines, cross-document consistency, RLS.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GoodsReceiptSchemaTests(PostgresFixture postgres)
{
    private static async Task<(TestHarness H, TestReceiving R, Guid Po, Guid Gr)> ReceivedAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var today = Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "c", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", 10m, 1500m)]), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        var line = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
        var gr = (await h.RunAsync(new PostGoodsReceipt(h.CompanyId, r.Storekeeper, "gr", p.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-1), [new(line, 3m)]), new PostGoodsReceiptHandler())).ResultRef;
        return (h, r, po, gr);
    }

    [Theory]
    [InlineData("UPDATE pur.goods_receipt_line SET qty = qty")]
    [InlineData("DELETE FROM pur.goods_receipt_line")]
    [InlineData("DELETE FROM pur.goods_receipt")]
    [InlineData("UPDATE pur.goods_receipt SET location_id = location_id, occurred_at = now(), version = version + 1")]
    [InlineData("UPDATE pur.goods_receipt SET document_status = 'POSTED', version = version")]
    public async Task Receipts_are_protected_even_for_the_owner(string sql)
    {
        var (h, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pur.goods_receipt_line SET qty = qty")]
    [InlineData("UPDATE pur.goods_receipt SET gr_no = gr_no")]
    [InlineData("DELETE FROM pur.goods_receipt")]
    public async Task Application_role_cannot_bypass_the_command(string sql)
    {
        var (h, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState);
        }
    }

    [Fact]
    public async Task K25_posted_receipt_without_its_journal_fails_at_commit()
    {
        var (h, r, po, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            var gr = Guid.CreateVersion7();
            await using var connection = await h.Admin.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();

            // The PO's creation event has no AUTO journal: claiming POSTED with it must fail at COMMIT (history row provided).
#pragma warning disable CA2100 // Test SQL.
            await using (var insert = new Npgsql.NpgsqlCommand(
                $"""
                INSERT INTO core.state_history (state_history_id, company_id, aggregate_type, aggregate_id, status_kind, from_state, to_state, command, event_id)
                SELECT gen_random_uuid(), company_id, 'GoodsReceipt', '{gr}', 'DOCUMENT', NULL, 'POSTED', 'x', event_id FROM core.domain_event WHERE event_type = 'PurchaseOrderCreated';
                INSERT INTO pur.goods_receipt (gr_id, company_id, gr_no, po_id, location_id, document_status, accounting_status, posting_event_id, occurred_at, version)
                SELECT '{gr}', company_id, 'RM-2026-FFFFFFFF', '{po}', '{r.LocationA}', 'POSTED', 'POSTED', event_id, now(), 1 FROM core.domain_event WHERE event_type = 'PurchaseOrderCreated';
                """,
                connection,
                tx))
#pragma warning restore CA2100
            {
                await insert.ExecuteNonQueryAsync();
            }

            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => tx.CommitAsync());

            Assert.Contains("without its journal (K-25)", ex.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Receipt_location_must_be_in_the_order_plant_and_lines_must_be_of_the_order()
    {
        var (h, _, po, gr) = await ReceivedAsync(postgres);
        await using (h)
        {
            var foreignLocation = await h.CreateLocationAsync(await h.CreatePlantAsync(), "OTRA");
            var header = await h.AdminExecuteAsync(
                $"INSERT INTO pur.goods_receipt (gr_id, company_id, gr_no, po_id, location_id, document_status, accounting_status, posting_event_id, occurred_at, version) SELECT gen_random_uuid(), company_id, 'RM-2026-EEEEEEEE', '{po}', '{foreignLocation}', 'POSTED', 'POSTED', posting_event_id, now(), 1 FROM pur.goods_receipt WHERE gr_id = '{gr}'");

            Assert.Contains("is not in the plant of purchase order", header!.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Row_level_security_isolates_receipts()
    {
        var (h, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var (connection, tx) = await h.OpenAppTransactionAsync(other);
            await using (connection)
            await using (tx)
            {
                await using var count = new Npgsql.NpgsqlCommand("SELECT (SELECT count(*) FROM pur.goods_receipt) + (SELECT count(*) FROM pur.goods_receipt_line)", connection, tx);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }
    }
}
