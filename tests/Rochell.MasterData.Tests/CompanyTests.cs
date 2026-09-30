using System.Text.Json;
using Rochell.MasterData.Company;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>UX2-01 (E-UX2-11): Configuración › Empresa — the legal name and the plants' names change on screen; the RNC never does.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CompanyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_controller_renames_the_company_and_a_plant_and_each_change_is_an_event()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        await h.RunAsync(new UpdateCompanyLegalName(h.CompanyId, controller, "c-1", "  Block Rochell, S.R.L.  "), new UpdateCompanyLegalNameHandler());
        await h.RunAsync(new UpdatePlantName(h.CompanyId, controller, "p-1", plant, "Planta Higüey"), new UpdatePlantNameHandler());
        var company = JsonDocument.Parse(await h.QueryAsync(new GetCompany(h.CompanyId, controller), new GetCompanyHandler())).RootElement;

        Assert.Equal("Block Rochell, S.R.L.", company.GetProperty("legalName").GetString());
        Assert.Equal("Planta Higüey", company.GetProperty("plants")[0].GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(company.GetProperty("rnc").GetString()));
        Assert.Equal("CompanyLegalNameChanged,PlantNameChanged", await h.ScalarAsync<string>(
            "SELECT string_agg(event_type, ',' ORDER BY event_type) FROM core.domain_event WHERE aggregate_type IN ('Company', 'Plant')"));
    }

    [Fact]
    public async Task Names_are_required_only_the_controller_edits_and_the_database_keeps_the_RNC()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var blank = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UpdateCompanyLegalName(h.CompanyId, controller, "c-1", "   "), new UpdateCompanyLegalNameHandler()));
        var notAllowed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UpdateCompanyLegalName(h.CompanyId, buyer, "c-2", "Otra"), new UpdateCompanyLegalNameHandler()));
        var noPlant = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UpdatePlantName(h.CompanyId, controller, "p-1", Guid.NewGuid(), "Planta"), new UpdatePlantNameHandler()));
        var rnc = await h.AdminExecuteAsync($"UPDATE md.company SET rnc = '101010101' WHERE company_id = '{h.CompanyId}'");

        Assert.Equal((MasterDataErrors.FieldRequired, AuthorizationErrors.NotAuthorized, MasterDataErrors.NotFound), (blank.Code, notAllowed.Code, noPlant.Code));
        Assert.Equal(SqlStates.RaiseException, rnc?.SqlState);
    }
}
