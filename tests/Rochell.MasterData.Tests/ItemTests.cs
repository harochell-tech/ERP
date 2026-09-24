using Rochell.MasterData.Items;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>Raw materials and UOM conversions (E-PR04-1, E-PR04-7, E-PR04-8, TMP-01).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ItemTests(PostgresFixture postgres)
{
    private static Task<CommandResult> CreateItem(TestHarness h, Guid session, string key, string code = "arena-lavada", string uom = "t", string category = "AGREGADO")
        => h.RunAsync(new CreateRawMaterial(h.CompanyId, session, key, code, "Arena lavada de río", uom, category), new CreateRawMaterialHandler());

    private static Task<CommandResult> Convert(TestHarness h, Guid session, string key, Guid item, string from, decimal factor, DateOnly from1)
        => h.RunAsync(new DefineUomConversion(h.CompanyId, session, key, item, from, factor, from1), new DefineUomConversionHandler());

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    [Fact]
    public async Task Raw_material_is_created_in_draft_with_upper_case_code()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");

        var result = await CreateItem(h, storekeeper, "i-1");

        Assert.True(await h.ScalarAsync<bool>(
            "SELECT code = 'ARENA-LAVADA' AND item_type = 'RAW_MATERIAL' AND base_uom = 't' AND item_category = 'AGREGADO' AND status = 'DRAFT' FROM md.item WHERE item_id = @i",
            ("i", result.ResultRef)));
    }

    [Theory]
    [InlineData("A", "t", "AGREGADO", MasterDataErrors.ItemCodeInvalid)]
    [InlineData("CEM-01", "t", "Cemento", MasterDataErrors.CategoryInvalid)]
    [InlineData("CEM-01", "ton", "CEMENTO", MasterDataErrors.UomUnknown)]
    public async Task Invalid_items_are_rejected(string code, string uom, string category, string expectedCode)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");

        var ex = await Assert.ThrowsAsync<DomainException>(() => CreateItem(h, storekeeper, "bad-" + code + uom + category, code, uom, category));

        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public async Task Duplicate_item_code_is_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");
        await CreateItem(h, storekeeper, "d-1");

        var ex = await Assert.ThrowsAsync<DomainException>(() => CreateItem(h, storekeeper, "d-2"));

        Assert.Equal(MasterDataErrors.ItemCodeDuplicate, ex.Code);
    }

    [Fact]
    public async Task Storekeeper_creates_but_only_the_controller_activates_and_defines_conversions()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var item = (await CreateItem(h, storekeeper, "p-1")).ResultRef;

        var activate = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new ActivateItem(h.CompanyId, storekeeper, "p-2", item, 1), new ActivateItemHandler()));
        var convert = await Assert.ThrowsAsync<DomainException>(() => Convert(h, storekeeper, "p-3", item, "kg", 0.001m, Today(h)));
        await Convert(h, controller, "p-4", item, "m3", 1.45m, Today(h));
        await h.RunAsync(new ActivateItem(h.CompanyId, controller, "p-5", item, 1), new ActivateItemHandler());

        Assert.Equal(AuthorizationErrors.NotAuthorized, activate.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, convert.Code);
        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status::text FROM md.item WHERE item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task New_conversion_closes_the_open_one_and_never_goes_back_in_time()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var item = (await CreateItem(h, storekeeper, "c-1")).ResultRef;
        var today = Today(h);

        await Convert(h, controller, "c-2", item, "m3", 1.45m, today);
        var sameDay = await Assert.ThrowsAsync<DomainException>(() => Convert(h, controller, "c-3", item, "m3", 1.50m, today));
        var yesterday = await Assert.ThrowsAsync<DomainException>(() => Convert(h, controller, "c-4", item, "m3", 1.50m, today.AddDays(-1)));
        await Convert(h, controller, "c-5", item, "m3", 1.50m, today.AddDays(10));

        Assert.Equal(MasterDataErrors.ConversionRetroactive, sameDay.Code);
        Assert.Equal(MasterDataErrors.ConversionRetroactive, yesterday.Code);
        Assert.Equal(
            $"1.45000000|{today:yyyy-MM-dd}|{today.AddDays(10):yyyy-MM-dd};1.50000000|{today.AddDays(10):yyyy-MM-dd}|open",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(factor::text || '|' || effective_from::text || '|' || coalesce(effective_to::text, 'open'), ';' ORDER BY effective_from)
                FROM md.uom_conversion WHERE item_id = @i
                """,
                ("i", item)));
    }

    [Theory]
    [InlineData("t", 1)]              // same as base UOM
    [InlineData("m3", 0)]             // not positive
    [InlineData("m3", -2)]
    public async Task Invalid_conversions_are_rejected(string from, int factor)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var item = (await CreateItem(h, storekeeper, "x-1")).ResultRef;

        var ex = await Assert.ThrowsAsync<DomainException>(() => Convert(h, controller, $"x-{from}-{factor}", item, from, factor, Today(h)));

        Assert.Equal(MasterDataErrors.ConversionInvalid, ex.Code);
    }

    [Fact]
    public async Task TMP01_overlapping_conversions_are_rejected_by_the_database()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");
        var item = (await CreateItem(h, storekeeper, "o-1")).ResultRef;
        var insert = $"INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '{item}', 'm3', 't', 1.45, '2030-01-01', NULL)";

        Assert.Null(await h.AdminExecuteAsync(insert));
        var overlap = await h.AdminExecuteAsync(insert.Replace("'2030-01-01'", "'2030-06-01'", StringComparison.Ordinal));
        var toNonBase = await h.AdminExecuteAsync($"INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '{item}', 'm3', 'kg', 1450, '2031-01-01', NULL)");

        Assert.Equal("23P01", overlap?.SqlState); // exclusion_violation
        Assert.Contains("base UOM", toNonBase!.MessageText, StringComparison.Ordinal);
    }
}
