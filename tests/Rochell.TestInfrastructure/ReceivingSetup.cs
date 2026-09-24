namespace Rochell.TestInfrastructure;

/// <summary>Receiving fixtures on top of purchasing: locations, RAW_MATERIAL and GRNI accounts and maps, periods, R-01 and a storekeeper.</summary>
public sealed record TestReceiving(TestPurchasing Purchasing, Guid LocationA, Guid LocationB, Guid RawMaterialAccount, Guid GrniAccount, Guid Storekeeper);

public static class ReceivingSetup
{
    public const string R01 = "0192f001-0000-7000-8000-000000000001";

    public static async Task<TestReceiving> CreateReceivingSetupAsync(this TestHarness h, bool approveR01 = true, bool mapGrni = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        var p = await h.CreatePurchasingSetupAsync();
        var locationA = await h.CreateLocationAsync(p.PlantId, "PATIO-A");
        var locationB = await h.CreateLocationAsync(p.PlantId, "PATIO-B");
        var raw = await h.CreateAccountAsync("1301", "Inventario de materia prima", isControl: true);
        var grni = await h.CreateAccountAsync("2105", "Recibido no facturado", isControl: false);
        await h.CreateActiveMapAsync("RAW_MATERIAL", raw);
        if (mapGrni)
        {
            await h.CreateActiveMapAsync("GRNI", grni);
        }

        var year = h.Clock.UtcNow.Year;
        for (var y = year - 1; y <= year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        if (approveR01)
        {
            await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R01}' AND version = 1");
        }

        return new TestReceiving(p, locationA, locationB, raw, grni, await h.SessionWithRolesAsync("ALMACENISTA"));
    }
}
