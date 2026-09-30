using System.Text.Json;
using Rochell.MasterData.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>UX3-01 (E-UX3-11): the units of measure, for pickers instead of free text.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class UomTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_units_of_measure_are_listed_with_their_dimension()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync();
        var storekeeper = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, storekeeper, "ALMACENISTA", plant);
        var session = await h.CreateSessionAsync(storekeeper);

        var items = JsonDocument.Parse(await h.QueryAsync(new ListUoms(h.CompanyId, session, plant), new ListUomsHandler())).RootElement.GetProperty("items");

        // 0005 seeds kg and t (MASS), m3 and l (VOLUME), un (COUNT); listed by dimension, then code.
        Assert.Equal("un:COUNT,kg:MASS,t:MASS,l:VOLUME,m3:VOLUME",
            string.Join(',', items.EnumerateArray().Select(u => $"{u.GetProperty("code").GetString()}:{u.GetProperty("dimension").GetString()}")));
    }
}
