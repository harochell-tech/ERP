using System.Text.Json;
using Rochell.Identity.Authorization;
using Rochell.Manufacturing.Portal;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>MFG2-03 (E-MFG2-3, E-MFG2-01-3/4/7): the plant manager pairs the portal; everyone reading production sees the pairings and the state.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PortalSetupTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_plant_manager_pairs_machines_moulds_shifts_and_materials_and_the_setup_shows_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);

        await h.RunAsync(new SetPortalMachine(h.CompanyId, s.Manager, "m", " Planta2 ", s.Machine, "Dosificadora2"), new SetPortalMachineHandler());
        await h.RunAsync(new SetPortalMould(h.CompanyId, s.Manager, "o", "6", s.Block), new SetPortalMouldHandler());
        await h.RunAsync(new SetPortalShift(h.CompanyId, s.Manager, "s", 1, s.Day), new SetPortalShiftHandler());
        await h.RunAsync(new SetPortalMaterial(h.CompanyId, s.Manager, "c", "dosificadora2", "cemento", s.Cement, "kg", s.Patio), new SetPortalMaterialHandler());
        await h.RunAsync(new SetPortalMaterial(h.CompanyId, s.Manager, "a", "dosificadora2", "ARENA", s.Sand, "m3", s.Patio), new SetPortalMaterialHandler());
        await h.RunAsync(new SetPortalMachine(h.CompanyId, s.Manager, "m2", "planta2", s.Machine, null), new SetPortalMachineHandler()); // offline now
        var setup = JsonDocument.Parse(await h.QueryAsync(new GetPortalSetup(h.CompanyId, s.Supervisor), new GetPortalSetupHandler())).RootElement;

        Assert.Equal(("planta2", "BESSER-1", JsonValueKind.Null), (setup.GetProperty("machines")[0].GetProperty("portalCode").GetString(),
            setup.GetProperty("machines")[0].GetProperty("machineCode").GetString(), setup.GetProperty("machines")[0].GetProperty("batchPlant").ValueKind));
        Assert.Equal(("6", "BLOQUE-6"), (setup.GetProperty("moulds")[0].GetProperty("mould").GetString(), setup.GetProperty("moulds")[0].GetProperty("itemCode").GetString()));
        Assert.Equal((1, "DIA"), (setup.GetProperty("shifts")[0].GetProperty("shiftNo").GetInt32(), setup.GetProperty("shifts")[0].GetProperty("shiftCode").GetString()));
        Assert.Equal("ARENA:m3,CEMENTO:kg", string.Join(',', setup.GetProperty("materials").EnumerateArray().Select(m => $"{m.GetProperty("materialCode").GetString()}:{m.GetProperty("uom").GetString()}")));
        Assert.Equal(6L, await h.ScalarAsync<long>("SELECT count(*) FROM core.domain_event WHERE event_type = 'PortalPairingChanged'"));
    }

    [Fact]
    public async Task Pairings_are_the_plant_managers_and_bad_ones_are_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);
        await h.RunAsync(new SetPortalMachine(h.CompanyId, s.Manager, "m", "planta2", s.Machine, null), new SetPortalMachineHandler());

        var supervisor = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetPortalMould(h.CompanyId, s.Supervisor, "x", "6", s.Block), new SetPortalMouldHandler()));
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetPortalMachine(h.CompanyId, s.Manager, "m2", "planta3", s.Machine, null), new SetPortalMachineHandler()));
        var notGood = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetPortalMould(h.CompanyId, s.Manager, "o", "6", s.Cement), new SetPortalMouldHandler()));
        var badUnit = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new SetPortalMaterial(h.CompanyId, s.Manager, "c", "dosificadora2", "CEMENTO", s.Cement, "t", s.Patio), new SetPortalMaterialHandler()));
        var removed = JsonDocument.Parse((await h.RunAsync(new RemovePortalPairing(h.CompanyId, s.Manager, "r", "MACHINE", "planta2"), new RemovePortalPairingHandler())).ResultPayload).RootElement;
        var gone = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RemovePortalPairing(h.CompanyId, s.Manager, "r2", "MACHINE", "planta2"), new RemovePortalPairingHandler()));

        Assert.Equal(AuthorizationErrors.NotAuthorized, supervisor.Code);
        Assert.Equal((ManufacturingErrors.PortalPairingInvalid, ManufacturingErrors.PortalPairingInvalid), (twice.Code, notGood.Code));
        Assert.Equal(ManufacturingErrors.UomNotConvertible, badUnit.Code);
        Assert.Equal(1, removed.GetProperty("removed").GetInt32());
        Assert.Equal(ManufacturingErrors.NotFound, gone.Code);
    }
}
