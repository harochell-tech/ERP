namespace Rochell.TestInfrastructure;

/// <summary>Inventory fixtures: plant with two locations, an ACTIVE raw material, RAW_MATERIAL mapping and the R-T1 rules.</summary>
public sealed record TestStock(TestLedger Ledger, Guid PlantId, Guid LocationA, Guid LocationB, Guid ItemId, Guid RawMaterialAccount);

public static class InventorySetup
{
    public static async Task<TestStock> CreateStockSetupAsync(this TestHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        var ledger = await h.CreateLedgerAsync();
        var locationA = await h.CreateLocationAsync(ledger.PlantId, "PATIO-A");
        var locationB = await h.CreateLocationAsync(ledger.PlantId, "PATIO-B");
        var item = await h.CreateActiveItemAsync("ARENA-LAVADA", "t", "AGREGADO");
        var raw = await h.CreateAccountAsync("1301", "Inventario de materia prima", isControl: true);
        await h.CreateActiveMapAsync("RAW_MATERIAL", raw);
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('0192f000-0000-7000-8000-0000000000f1', '0192f000-0000-7000-8000-0000000000f2') AND version = 1");
        return new TestStock(ledger, ledger.PlantId, locationA, locationB, item, raw);
    }

    public static async Task<Guid> CreateActiveItemAsync(this TestHarness h, string code, string baseUom, string category)
    {
        ArgumentNullException.ThrowIfNull(h);
        var id = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"INSERT INTO md.item VALUES ('{id}', '{h.CompanyId}', '{code}', '{code}', 'RAW_MATERIAL', '{baseUom}', '{category}', 'ACTIVE', 1)");
        return id;
    }
}
