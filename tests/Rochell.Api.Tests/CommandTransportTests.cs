using System.Net;
using System.Text;
using Rochell.Api.Http;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-PR18-3: command routes, Idempotency-Key, body binding, problem+json status codes, correlation and request_log.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CommandTransportTests(PostgresFixture postgres)
{
    private const string MasterData = "master-data";

    private static object Supplier(string rnc = "130000011", string name = "Agregados del Este, S.R.L.") => new { rnc, legalName = name };

    [Fact]
    public async Task A_command_returns_its_result_and_a_repeated_key_replays_it_without_running_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));

        var first = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier(), "alta-proveedor-1");
        var again = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier(), "alta-proveedor-1");

        var one = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        var two = System.Text.Json.JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(one.GetProperty("replayed").GetBoolean());
        Assert.False(first.Headers.Contains(CommandRunner.ReplayedHeader));
        Assert.Equal("DRAFT", one.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(one.GetProperty("resultRef").GetGuid(), one.GetProperty("result").GetProperty("partyId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(two.GetProperty("replayed").GetBoolean());
        Assert.Equal("true", Assert.Single(again.Headers.GetValues(CommandRunner.ReplayedHeader)));
        Assert.Equal(one.GetProperty("commandId").GetGuid(), two.GetProperty("commandId").GetGuid());
        Assert.Equal(1L, await h.CountAsync("md.party"));
    }

    [Fact]
    public async Task The_correlation_id_travels_to_the_command_log_events_and_request_log()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));
        var correlation = Guid.CreateVersion7();
        buyer.DefaultRequestHeaders.Add(Correlation.Header, correlation.ToString());

        var response = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier(), "alta-correlacion");

        Assert.Equal(correlation.ToString(), Assert.Single(response.Headers.GetValues(Correlation.Header)));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.domain_event WHERE correlation_id = @c", ("c", correlation)));
        await WaitForAsync(async () => await h.ScalarAsync<long>("SELECT count(*) FROM obs.request_log WHERE correlation_id = @c AND outcome = 'SUCCEEDED'", ("c", correlation)) == 1);
    }

    [Fact]
    public async Task Transport_errors_are_400_problems_and_nothing_runs()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));

        var noKey = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier(), idempotencyKey: null);
        var longKey = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier(), new string('k', 201));
        var serverField = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", new { rnc = "130000011", legalName = "X", sessionId = h.SessionId }, "k1");
        var unknownField = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", new { rnc = "130000011", legalName = "X", nombre = "X" }, "k2");
        var missingField = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", new { rnc = "130000011" }, "k3");
        var notJson = await buyer.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/companies/{h.CompanyId}/{MasterData}/create-supplier")
        {
            Headers = { { CommandRunner.IdempotencyKeyHeader, "k4" } },
            Content = new StringContent("rnc=1", Encoding.UTF8, "application/json"),
        });

        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.IdempotencyKeyRequired), await noKey.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.IdempotencyKeyRequired), await longKey.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.InvalidRequest), await serverField.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.InvalidRequest), await unknownField.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.InvalidRequest), await missingField.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.InvalidRequest), await notJson.ProblemAsync());
        Assert.Equal(0L, await h.CountAsync("md.party"));
    }

    [Fact]
    public async Task Decimals_must_be_json_strings()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var p = await h.CreatePurchasingSetupAsync();
        var buyer = await api.SignInAsSessionUserAsync(p.Buyer);
        var today = BusinessCalendar.DefaultBusinessDate(DateTime.UtcNow);

        var asNumber = await buyer.CommandAsync(h.CompanyId, "procurement", "create-purchase-order", new
        {
            plantId = p.PlantId,
            partyId = p.SupplierId,
            orderDate = today,
            lines = new[] { new { itemId = p.Sand, uom = "t", quantity = 40, unitPrice = "1000.00" } },
        }, "oc-numero");
        var asString = await buyer.CommandAsync(h.CompanyId, "procurement", "create-purchase-order", new
        {
            plantId = p.PlantId,
            partyId = p.SupplierId,
            orderDate = today,
            lines = new[] { new { itemId = p.Sand, uom = "t", quantity = "40", unitPrice = "1000.00" } },
        }, "oc-texto");

        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.InvalidRequest), await asNumber.ProblemAsync());
        Assert.Equal(HttpStatusCode.OK, asString.StatusCode);
        Assert.Equal("40.000000|1000.000000", await h.ScalarAsync<string>("SELECT qty_ordered || '|' || unit_price FROM pur.purchase_order_line"));
    }

    [Theory]
    [InlineData("2026-09-24T10:00:00")]
    [InlineData("2026-09-24")]
    public async Task Timestamps_need_an_offset(string occurredAt)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));

        var response = await storekeeper.CommandAsync(h.CompanyId, "procurement", "post-goods-receipt", new
        {
            plantId = Guid.CreateVersion7(),
            purchaseOrderId = Guid.CreateVersion7(),
            locationId = Guid.CreateVersion7(),
            occurredAt,
            lines = new[] { new { purchaseOrderLineId = Guid.CreateVersion7(), quantity = "1" } },
        }, "sin-zona");

        Assert.Equal((HttpStatusCode.BadRequest, ApiErrors.InvalidRequest), await response.ProblemAsync());
    }

    [Fact]
    public async Task Domain_errors_map_to_401_403_409_and_422()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var created = await buyer.OkAsync(h.CompanyId, MasterData, "create-supplier", Supplier());
        var partyId = created.GetProperty("resultRef").GetGuid();

        var anonymous = await api.Browser().CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier("130000029"), "anon");
        var forbidden = await storekeeper.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier("130000029"), "sin-permiso");
        var otherCompany = await buyer.CommandAsync(await h.CreateCompanyAsync(), MasterData, "create-supplier", Supplier("130000029"), "otra-empresa");
        var stale = await buyer.CommandAsync(h.CompanyId, MasterData, "update-supplier", new { partyId, expectedVersion = 7, rnc = "130000011", legalName = "Nuevo nombre" }, "version-vieja");
        var invalid = await buyer.CommandAsync(h.CompanyId, MasterData, "create-supplier", Supplier("12"), "rnc-corto");

        Assert.Equal((HttpStatusCode.Unauthorized, "SESSION_INVALID"), await anonymous.ProblemAsync());
        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await forbidden.ProblemAsync());
        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await otherCompany.ProblemAsync());
        Assert.Equal((HttpStatusCode.Conflict, "VERSION_CONFLICT"), await stale.ProblemAsync());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "RNC_INVALID"), await invalid.ProblemAsync());
        Assert.Equal(1L, await h.CountAsync("md.party"));
    }

    [Fact]
    public async Task A_step_up_action_needs_a_recent_re_authentication_through_prompt_login()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, clock);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));
        var controllerUser = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", await h.SessionWithRolesAsync("CONTROLLER")));
        var controller = await api.SignInAsync(controllerUser);
        var partyId = (await buyer.OkAsync(h.CompanyId, MasterData, "create-supplier", Supplier())).GetProperty("resultRef").GetGuid();
        clock.Advance(TimeSpan.FromMinutes(6)); // older than the 5-minute step-up window (E-PR03-2)

        var refused = await controller.CommandAsync(h.CompanyId, MasterData, "activate-supplier", new { partyId, expectedVersion = 1 }, "activar");
        var callback = await api.StartAsync(controller, "/api/v1/auth/step-up?returnUrl=/proveedores", controllerUser);
        var prompt = api.Idp.LastAuthorizeRequest.GetValueOrDefault("prompt");
        var maxAge = api.Idp.LastAuthorizeRequest.GetValueOrDefault("max_age");
        var reauthenticated = await controller.GetAsync(callback);
        var accepted = await controller.CommandAsync(h.CompanyId, MasterData, "activate-supplier", new { partyId, expectedVersion = 1 }, "activar");

        Assert.Equal((HttpStatusCode.Forbidden, "STEP_UP_REQUIRED"), await refused.ProblemAsync());
        Assert.Equal("login", prompt);
        Assert.Equal("0", maxAge);
        Assert.Equal(HttpStatusCode.OK, reauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status::text FROM md.party"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.session WHERE user_id = @u", ("u", controllerUser))); // fixture session + browser session: step-up opens none
    }

    [Fact]
    public async Task A_step_up_by_another_google_account_is_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var controllerUser = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, controllerUser, "CONTROLLER");
        var intruder = await h.CreateUserAsync();
        var controller = await api.SignInAsync(controllerUser);
        var before = await h.ScalarAsync<DateTime>("SELECT last_step_up_at FROM iam.session WHERE user_id = @u", ("u", controllerUser));

        var callback = await api.StartAsync(controller, "/api/v1/auth/step-up", intruder);
        var page = await controller.GetAsync(callback);

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal(before, await h.ScalarAsync<DateTime>("SELECT last_step_up_at FROM iam.session WHERE user_id = @u", ("u", controllerUser)));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Condition not reached within 5 s.");
    }
}
