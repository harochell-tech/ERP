using System.Text.Json;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>UX2-01 (E-UX2-9/10): the 19 setup steps, computed from the data as the application role.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SetupStatusTests(PostgresFixture postgres)
{
    private static async Task<Dictionary<string, (string Status, string Missing)>> StepsAsync(TestHarness h, Guid session)
    {
        var root = JsonDocument.Parse(await h.QueryAsync(new GetSetupStatus(h.CompanyId, session), new GetSetupStatusHandler())).RootElement;
        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(1, 19), steps.Select(s => s.GetProperty("order").GetInt32()));
        Assert.False(root.GetProperty("complete").GetBoolean());
        return steps.ToDictionary(
            s => s.GetProperty("code").GetString()!,
            s => (s.GetProperty("status").GetString()!, string.Join(',', s.GetProperty("missing").EnumerateArray().Select(m => m.GetString()))));
    }

    [Fact]
    public async Task A_new_company_starts_pending_and_each_step_follows_its_data()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var fresh = await StepsAsync(h, controller);
        var plant = await h.CreatePlantAsync();
        var unnamed = await StepsAsync(h, controller);
        await h.AdminExecuteAsync($"UPDATE md.plant SET name = 'Planta Higüey' WHERE plant_id = '{plant}'");
        var named = await StepsAsync(h, controller);

        Assert.Equal("DONE", fresh["COMPANY"].Status);
        Assert.Equal(("PENDING", "BANK_ACCOUNT"), fresh["BANK_ACCOUNTS"]);
        Assert.Equal(("WARNING", "ADMIN_SEGURIDAD,SEGUNDO_APROBADOR_SEGURIDAD"), fresh["USERS"]); // the Controller is there
        Assert.Equal(("PENDING", "BALANCE_SHEET,INCOME_STATEMENT"), fresh["REPORT_STRUCTURES"]);
        Assert.NotEqual("DONE", fresh["POLICIES"].Status);
        Assert.Equal("WARNING", unnamed["PLANTS"].Status);
        Assert.NotEqual(string.Empty, unnamed["PLANTS"].Missing);
        Assert.Equal(("DONE", string.Empty), named["PLANTS"]);
    }
}
