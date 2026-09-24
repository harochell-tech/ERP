using System.Text.Json;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>RC-06 (approver ≠ creator in the database), §11.3 transitions, identity immutability, privileges, RLS.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReceiptCorrectionSchemaTests(PostgresFixture postgres)
{
    private static async Task<(TestHarness H, Guid Rc)> PendingAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var p = r.Purchasing;
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "c", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", 20m, 1500m)]), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        var poLine = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
        var gr = await h.RunAsync(new PostGoodsReceipt(h.CompanyId, r.Storekeeper, "gr", p.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-1), [new(poLine, 20m)]), new PostGoodsReceiptHandler());
        var grLine = JsonDocument.Parse(gr.ResultPayload).RootElement.GetProperty("lines")[0].GetProperty("grLineId").GetGuid();
        var rc = await h.RunAsync(new CreateReceiptCorrection(h.CompanyId, r.Storekeeper, "rc", p.PlantId, gr.ResultRef, grLine, -8m, "x", "BAS-1"), new CreateReceiptCorrectionHandler());
        return (h, rc.ResultRef);
    }

    [Fact]
    public async Task RC06_the_creator_can_never_be_the_approver()
    {
        var (h, rc) = await PendingAsync(postgres);
        await using (h)
        {
            var ex = await h.AdminExecuteAsync($"UPDATE pur.receipt_correction SET approved_by = created_by, version = version + 1 WHERE rc_id = '{rc}'");

            Assert.Equal(SqlStates.CheckViolation, ex?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pur.receipt_correction SET delta_qty = -1, version = version + 1")]
    [InlineData("UPDATE pur.receipt_correction SET document_status = 'DRAFT', version = version + 1")]
    [InlineData("UPDATE pur.receipt_correction SET version = version")]
    [InlineData("DELETE FROM pur.receipt_correction")]
    public async Task Corrections_are_protected_even_for_the_owner(string sql)
    {
        var (h, _) = await PendingAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pur.receipt_correction SET delta_qty = delta_qty")]
    [InlineData("DELETE FROM pur.receipt_correction")]
    public async Task Application_role_cannot_bypass_the_commands(string sql)
    {
        var (h, _) = await PendingAsync(postgres);
        await using (h)
        {
            Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState);
        }
    }

    [Fact]
    public async Task Row_level_security_isolates_corrections()
    {
        var (h, _) = await PendingAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var (connection, tx) = await h.OpenAppTransactionAsync(other);
            await using (connection)
            await using (tx)
            {
                await using var count = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pur.receipt_correction", connection, tx);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }
    }
}
