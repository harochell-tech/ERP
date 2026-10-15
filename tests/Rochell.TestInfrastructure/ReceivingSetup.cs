namespace Rochell.TestInfrastructure;

/// <summary>Receiving fixtures on top of purchasing: locations, RAW_MATERIAL and GRNI accounts and maps, periods, R-01 and a storekeeper.</summary>
/// <remarks>REC-01 (E-REC-1): <c>Vehicle</c> and <c>Driver</c> are our truck and driver, named on every receipt.</remarks>
public sealed record TestReceiving(TestPurchasing Purchasing, Guid LocationA, Guid LocationB, Guid RawMaterialAccount, Guid GrniAccount, Guid Storekeeper, Guid Vehicle, Guid Driver);

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

        var vehicle = Guid.CreateVersion7();
        var driver = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO log.vehicle (vehicle_id, company_id, plate, capacity_kg, status, version) VALUES ('{vehicle}', '{h.CompanyId}', 'L{vehicle.ToString("N")[..6].ToUpperInvariant()}', 12000, 'ACTIVE', 1);
            INSERT INTO log.driver (driver_id, company_id, full_name, national_id, status, version) VALUES ('{driver}', '{h.CompanyId}', 'Chofer de recepción', '{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}', 'ACTIVE', 1);
            """);
        return new TestReceiving(p, locationA, locationB, raw, grni, await h.SessionWithRolesAsync("ALMACENISTA"), vehicle, driver);
    }

    /// <summary>REC-01 (E-REC-1/2): the receipt names the fixture's truck and driver and a supplier's delivery note of its own.</summary>
    public static Rochell.Procurement.GoodsReceipts.PostGoodsReceipt WithTransport(this Rochell.Procurement.GoodsReceipts.PostGoodsReceipt receipt, TestReceiving receiving, string? deliveryNote = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(receiving);
        return receipt with
        {
            VehicleId = receipt.VehicleId ?? receiving.Vehicle,
            DriverId = receipt.DriverId ?? receiving.Driver,
            SupplierDeliveryNote = receipt.SupplierDeliveryNote ?? deliveryNote ?? "CP-" + Guid.CreateVersion7().ToString("N")[^12..],
        };
    }
}
