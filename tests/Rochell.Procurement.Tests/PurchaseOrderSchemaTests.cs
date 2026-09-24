using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>Database guards of the purchase order: ADR-027 history, §11.1 transitions, CHECKs, company-safe keys, RLS.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PurchaseOrderSchemaTests(PostgresFixture postgres)
{
    private static async Task<(TestHarness H, TestPurchasing P, Guid Po)> DraftAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var result = await h.RunAsync(
            new CreatePurchaseOrder(h.CompanyId, p.Buyer, "seed", p.PlantId, p.SupplierId, BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow), [new(p.Sand, "t", 10m, 100m)]),
            new CreatePurchaseOrderHandler());
        return (h, p, result.ResultRef);
    }

    [Fact]
    public async Task Status_change_without_state_history_fails_at_commit()
    {
        var (h, _, po) = await DraftAsync(postgres);
        await using (h)
        {
            var ex = await h.AdminExecuteAsync($"UPDATE pur.purchase_order SET status = 'PENDING_APPROVAL', version = version + 1 WHERE po_id = '{po}'");

            Assert.Contains("state_history", ex!.MessageText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("RECEIVED")]
    [InlineData("CLOSED")]
    [InlineData("APPROVED")]
    public async Task Transitions_outside_the_state_machine_are_rejected(string target)
    {
        var (h, _, po) = await DraftAsync(postgres);
        await using (h)
        {
            var ex = await h.AdminExecuteAsync($"UPDATE pur.purchase_order SET status = '{target}', version = version + 1 WHERE po_id = '{po}'");

            Assert.Equal(SqlStates.RaiseException, ex?.SqlState);
        }
    }

    [Theory]
    [InlineData("DELETE FROM pur.purchase_order")]
    [InlineData("UPDATE pur.purchase_order SET party_id = party_id, version = version + 1")]
    [InlineData("UPDATE pur.purchase_order_line SET unit_price = 1, version = version + 1")]
    [InlineData("UPDATE pur.purchase_order SET version = version")]
    public async Task Identity_and_history_are_protected_even_for_the_owner(string sql)
    {
        var (h, _, _) = await DraftAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pur.purchase_order SET po_no = po_no")]
    [InlineData("UPDATE pur.purchase_order_line SET unit_price = unit_price")]
    [InlineData("DELETE FROM pur.purchase_order")]
    public async Task Application_role_cannot_bypass_the_commands(string sql)
    {
        var (h, _, _) = await DraftAsync(postgres);
        await using (h)
        {
            Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState);
        }
    }

    [Fact]
    public async Task Orders_cannot_reference_suppliers_or_plants_of_another_company()
    {
        var (h, p, _) = await DraftAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var foreignPlant = await h.CreatePlantAsync(other);

            var ex = await h.AdminExecuteAsync(
                $"INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version) VALUES (gen_random_uuid(), '{h.CompanyId}', 'OC-2026-FFFFFFFF', '{p.SupplierId}', '{foreignPlant}', current_date, 'DRAFT', '{h.UserId}', 1)");

            Assert.Equal(SqlStates.ForeignKeyViolation, ex?.SqlState);
        }
    }

    [Fact]
    public async Task Row_level_security_isolates_purchase_orders()
    {
        var (h, _, _) = await DraftAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var (connection, tx) = await h.OpenAppTransactionAsync(other);
            await using (connection)
            await using (tx)
            {
                await using var count = new Npgsql.NpgsqlCommand("SELECT (SELECT count(*) FROM pur.purchase_order) + (SELECT count(*) FROM pur.purchase_order_line)", connection, tx);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }
    }
}
