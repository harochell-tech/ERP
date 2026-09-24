namespace Rochell.TestInfrastructure;

/// <summary>Purchasing fixtures: plant, ACTIVE supplier, ACTIVE items (base t; m3 convertible), PURCHASING policy and the three actors.</summary>
public sealed record TestPurchasing(Guid PlantId, Guid SupplierId, Guid Sand, Guid Cement, Guid Buyer, Guid Approver, Guid Controller, Guid PolicyVersionId);

public static class ProcurementSetup
{
    public static async Task<TestPurchasing> CreatePurchasingSetupAsync(this TestHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        var plant = await h.CreatePlantAsync();
        var supplier = await h.CreateActiveSupplierAsync("101000011", "Agregados del Este, S.R.L.");
        var sand = await h.CreateActiveItemAsync("ARENA-LAVADA", "t", "AGREGADO");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");
        await h.AdminRequireAsync($"INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '{sand}', 'm3', 't', 1.45, '2020-01-01', NULL)");
        var policy = await h.CreateActivePolicyAsync("PURCHASING", PolicySetup.Purchasing);
        return new TestPurchasing(
            plant,
            supplier,
            sand,
            cement,
            await h.SessionWithRolesAsync("COMPRADOR"),
            await h.SessionWithRolesAsync("APROBADOR_COMPRAS"),
            await h.SessionWithRolesAsync("CONTROLLER"),
            policy);
    }

    public static async Task<Guid> CreateActiveSupplierAsync(this TestHarness h, string rnc, string name)
    {
        ArgumentNullException.ThrowIfNull(h);
        var id = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.party VALUES ('{id}', '{h.CompanyId}', 'LOCAL', '{rnc}', '{name}', true, 'ACTIVE', NULL, 1)");
        return id;
    }
}
