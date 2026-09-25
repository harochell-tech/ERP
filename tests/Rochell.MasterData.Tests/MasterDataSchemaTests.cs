using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>TEN-01 (company-safe FKs, Patch 1 P-5), E-PR03-1, guards, seeds and row-level security.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MasterDataSchemaTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";

    [Trait("Acceptance", "TEN-01")]
    [Fact]
    public async Task TEN01_masters_cannot_reference_rows_of_another_company()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var otherCompany = await h.CreateCompanyAsync();
        var otherPlant = await h.CreatePlantAsync(otherCompany);
        Assert.Null(await h.AdminExecuteAsync(
            $"INSERT INTO md.item VALUES ('0192f000-0000-7000-8000-00000000b001', '{otherCompany}', 'ARENA', 'Arena', 'RAW_MATERIAL', 't', 'AGREGADO', 'DRAFT', 1)"));
        Assert.Null(await h.AdminExecuteAsync($"INSERT INTO md.valuation_area VALUES ('{otherCompany}', '0192f000-0000-7000-8000-00000000b002', 'FREE')"));

        var location = await h.AdminExecuteAsync($"INSERT INTO md.location VALUES (gen_random_uuid(), '{h.CompanyId}', '{otherPlant}', 'REC')");
        var conversion = await h.AdminExecuteAsync($"INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '0192f000-0000-7000-8000-00000000b001', 'm3', 't', 1.4, '2030-01-01', NULL)");
        var plant = await h.AdminExecuteAsync($"INSERT INTO md.plant VALUES (gen_random_uuid(), '{h.CompanyId}', 'PX', '0192f000-0000-7000-8000-00000000b002')");
        var assignment = await h.AdminExecuteAsync(
            $"INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by) SELECT gen_random_uuid(), '{h.CompanyId}', '{h.UserId}', role_id, '{otherPlant}', now(), '00000000-0000-7000-8000-00000000d001' FROM iam.role WHERE code = 'COMPRADOR'");

        Assert.Equal(SqlStates.ForeignKeyViolation, location?.SqlState);
        Assert.Equal(SqlStates.ForeignKeyViolation, conversion?.SqlState);
        Assert.Equal(SqlStates.ForeignKeyViolation, plant?.SqlState);
        Assert.Equal(SqlStates.ForeignKeyViolation, assignment?.SqlState); // E-PR03-1
    }

    [Fact]
    public async Task Units_of_measure_are_seeded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal("kg:MASS,l:VOLUME,m3:VOLUME,t:MASS,un:COUNT", await h.ScalarAsync<string>(
            "SELECT string_agg(uom_code || ':' || dimension, ',' ORDER BY uom_code) FROM md.uom"));
    }

    [Theory]
    [InlineData("INSERT INTO md.party VALUES (gen_random_uuid(), '{0}', 'LOCAL', NULL, 'Sin RNC', true, 'DRAFT', NULL, 1)")]
    [InlineData("INSERT INTO md.party VALUES (gen_random_uuid(), '{0}', 'LOCAL', '1-01-00001-1', 'Con guiones', true, 'DRAFT', NULL, 1)")]
    [InlineData("INSERT INTO md.item VALUES (gen_random_uuid(), '{0}', 'PT-01', 'Block', 'FINISHED_GOOD', 'un', 'CEMENTO', 'DRAFT', 1)")]
    [InlineData("INSERT INTO md.item VALUES (gen_random_uuid(), '{0}', 'X-01', 'X', 'RAW_MATERIAL', 't', 'VARIOS', 'DRAFT', 1)")]
    public async Task Invalid_master_rows_are_rejected_by_check_constraints(string sqlTemplate)
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var ex = await h.AdminExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlTemplate, h.CompanyId));

        Assert.Equal(SqlStates.CheckViolation, ex?.SqlState);
    }

    [Fact]
    public async Task Foreign_supplier_without_rnc_is_allowed_by_the_schema()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Null(await h.AdminExecuteAsync(
            $"INSERT INTO md.party VALUES (gen_random_uuid(), '{h.CompanyId}', 'FOREIGN', NULL, 'Besser Company', true, 'DRAFT', NULL, 1)"));
    }

    [Fact]
    public async Task Active_masters_and_structure_cannot_be_modified_or_deleted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync();
        Assert.Null(await h.AdminExecuteAsync(
            $"INSERT INTO md.item VALUES ('0192f000-0000-7000-8000-00000000c001', '{h.CompanyId}', 'CEM-01', 'Cemento gris', 'RAW_MATERIAL', 't', 'CEMENTO', 'ACTIVE', 1)"));

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("UPDATE md.item SET description = 'x', version = 2"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("DELETE FROM md.item"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"UPDATE md.plant SET code = 'Z' WHERE plant_id = '{plant}'"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("DELETE FROM md.uom WHERE uom_code = 'kg'"))?.SqlState);
    }

    [Theory]
    [InlineData("INSERT INTO md.plant (plant_id, company_id, code, valuation_area_id) VALUES (gen_random_uuid(), '{0}', 'P9', gen_random_uuid())")]
    [InlineData("INSERT INTO md.uom VALUES ('lb', 'MASS')")]
    [InlineData("UPDATE md.item SET description = 'x'")]
    [InlineData("DELETE FROM md.party")]
    public async Task Application_role_cannot_change_structure_or_bypass_commands(string sqlTemplate)
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var ex = await h.AppExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlTemplate, h.CompanyId));

        Assert.Equal(InsufficientPrivilege, ex?.SqlState);
    }

    [Fact]
    public async Task Row_level_security_isolates_master_data()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var otherCompany = await h.CreateCompanyAsync();
        await h.CreatePlantAsync();
        await h.CreatePlantAsync(otherCompany);
        Assert.Null(await h.AdminExecuteAsync(
            $"INSERT INTO md.party VALUES (gen_random_uuid(), '{otherCompany}', 'LOCAL', '131000001', 'Ajeno', true, 'DRAFT', NULL, 1)"));

        var (connection, transaction) = await h.OpenAppTransactionAsync();
        await using (connection)
        await using (transaction)
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT (SELECT count(*) FROM md.plant) || '|' || (SELECT count(*) FROM md.party) || '|' || (SELECT count(*) FROM md.uom)", connection, transaction);
            Assert.Equal("1|0|5", await command.ExecuteScalarAsync());
        }
    }
}
