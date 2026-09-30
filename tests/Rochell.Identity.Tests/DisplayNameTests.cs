using System.Text.Json;
using Rochell.Identity.Queries;
using Rochell.Identity.RoleChanges;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>UX1-01a: people by the name Google gives and plants by their name (E-UX1-01-3, E-UX1-01-4).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DisplayNameTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_Google_name_is_kept_at_sign_in_refreshed_at_step_up_and_a_sign_in_without_one_leaves_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var claims = TestHarness.ClaimsOf(h.UserId);

        var session = await h.Sessions.StartOidcSessionAsync(claims with { Name = "  Ana Pérez  " }, null, "browser");
        var first = await h.Sessions.DescribeAsync(session);
        await h.Sessions.RecordStepUpAsync(session, claims with { Name = "Ana María Pérez" });
        var renamed = await h.ScalarAsync<string>("SELECT display_name FROM iam.user WHERE user_id = @u", ("u", h.UserId));
        await h.Sessions.StartOidcSessionAsync(claims, null, "browser");

        Assert.Equal(("Ana Pérez", claims.Email), (first.DisplayName, first.Email));
        Assert.Equal("Ana María Pérez", renamed);
        Assert.Equal("Ana María Pérez", await h.ScalarAsync<string>("SELECT display_name FROM iam.user WHERE user_id = @u", ("u", h.UserId)));
    }

    [Fact]
    public async Task Screens_get_the_name_where_they_showed_the_e_mail_and_the_session_lists_the_named_plants()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var admin = await h.CreateUserAsync();
        var target = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, admin, "ADMIN_SEGURIDAD");
        var adminSession = await h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(admin) with { Name = "Luis Gómez" }, null, "browser");
        await h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(target) with { Name = "Carla Díaz" }, null, "browser");
        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        await h.AdminRequireAsync($"UPDATE md.plant SET name = 'Planta Higüey' WHERE plant_id = '{plant}'");

        await h.RunAsync(new RequestRoleAssignment(h.CompanyId, adminSession, "req", target, "COMPRADOR"), new RequestRoleAssignmentHandler());
        var requests = JsonDocument.Parse(await h.Queries.ExecuteAsync(new ListRoleRequests(h.CompanyId, adminSession), new ListRoleRequestsHandler())).RootElement;
        var users = JsonDocument.Parse(await h.Queries.ExecuteAsync(new ListUsers(h.CompanyId, adminSession), new ListUsersHandler())).RootElement;
        var described = await h.Sessions.DescribeAsync(adminSession);

        var request = Assert.Single(requests.GetProperty("items").EnumerateArray());
        Assert.Equal("Luis Gómez|Carla Díaz", $"{request.GetProperty("requestedBy").GetString()}|{request.GetProperty("userDisplayName").GetString()}");
        Assert.Equal("Carla Díaz", users.GetProperty("items").EnumerateArray().Single(u => u.GetProperty("userId").GetGuid() == target).GetProperty("displayName").GetString());
        var codeChange = await h.AdminExecuteAsync($"UPDATE md.plant SET code = 'OTRA' WHERE plant_id = '{plant}'");
        var delete = await h.AdminExecuteAsync($"DELETE FROM md.plant WHERE plant_id = '{plant}'");
        Assert.Equal(("P0001", "P0001"), (codeChange?.SqlState, delete?.SqlState)); // only the name changes
        var plants = Assert.Single(described.Companies).Plants!;
        Assert.Equal("HIGUEY:Planta Higüey", string.Join('|', plants.Where(p => p.PlantId == plant).Select(p => $"{p.Code}:{p.Name}")));
    }
}
