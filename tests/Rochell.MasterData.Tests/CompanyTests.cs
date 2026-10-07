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

    /// <summary>E-VS4-03-2: the e-CF issuer's address, trade name, phone and e-mail.</summary>
    [Fact]
    public async Task The_controller_sets_the_issuer_address_and_contact_and_bad_values_are_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var noAddress = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new UpdateCompanyContact(h.CompanyId, controller, "k-0", " ", null, null, null), new UpdateCompanyContactHandler()));
        var badPhone = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new UpdateCompanyContact(h.CompanyId, controller, "k-1", "Higüey", null, "8095540000", null), new UpdateCompanyContactHandler()));
        await h.RunAsync(
            new UpdateCompanyContact(h.CompanyId, controller, "k-2", " Carretera Higüey–La Romana km 3 ", "Block Rochell", "809-554-0000", "Industrias@Rochell.com.do"),
            new UpdateCompanyContactHandler());
        var company = JsonDocument.Parse(await h.QueryAsync(new GetCompany(h.CompanyId, controller), new GetCompanyHandler())).RootElement;

        Assert.Equal((MasterDataErrors.FieldRequired, MasterDataErrors.FieldRequired), (noAddress.Code, badPhone.Code));
        Assert.Equal(("Carretera Higüey–La Romana km 3", "Block Rochell", "809-554-0000", "industrias@rochell.com.do"), (company.GetProperty("address").GetString(),
            company.GetProperty("tradeName").GetString(), company.GetProperty("phone").GetString(), company.GetProperty("email").GetString()));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.domain_event WHERE event_type = 'CompanyContactChanged'"));
    }
}
