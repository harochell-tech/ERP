using System.Text.Json;
using Rochell.Identity.Queries;
using Rochell.Identity.RoleChanges;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>UI-01: users and role requests read with iam:read (E-UI01-4) and the second approver's rejection (E-UI01-6 (a)).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RoleScreenTests(PostgresFixture postgres)
{
    private sealed record Actors(TestHarness H, Guid AdminSession, Guid ApproverSession, Guid Target);

    private async Task<Actors> ActorsAsync()
    {
        var h = await TestHarness.CreateAsync(postgres);
        var admin = await h.CreateUserAsync();
        var approver = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, admin, "ADMIN_SEGURIDAD");
        await h.GrantAsync(h.CompanyId, approver, "SEGUNDO_APROBADOR_SEGURIDAD");
        return new Actors(h, await h.CreateSessionAsync(admin), await h.CreateSessionAsync(approver), await h.CreateUserAsync());
    }

    private static async Task<JsonElement> Requests(Actors a, Guid session, string? status = null)
        => JsonDocument.Parse(await a.H.Queries.ExecuteAsync(new ListRoleRequests(a.H.CompanyId, session, status), new ListRoleRequestsHandler())).RootElement;

    [Fact]
    public async Task The_second_approver_rejects_a_request_with_a_reason_and_nothing_is_granted()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var request = (await h.RunAsync(new RequestRoleAssignment(h.CompanyId, a.AdminSession, "req", a.Target, "COMPRADOR"), new RequestRoleAssignmentHandler())).ResultRef;

        var pending = await Requests(a, a.ApproverSession, "REQUESTED");
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RejectRoleChange(h.CompanyId, a.ApproverSession, "rej-0", request, " "), new RejectRoleChangeHandler()));
        var requester = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RejectRoleChange(h.CompanyId, a.AdminSession, "rej-1", request, "No corresponde"), new RejectRoleChangeHandler()));
        await h.RunAsync(new RejectRoleChange(h.CompanyId, a.ApproverSession, "rej-2", request, "El puesto no requiere compras"), new RejectRoleChangeHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveRoleChange(h.CompanyId, a.ApproverSession, "app", request), new ApproveRoleChangeHandler()));
        var rejected = await Requests(a, a.ApproverSession, "REJECTED");

        var p = Assert.Single(pending.GetProperty("items").EnumerateArray());
        Assert.Equal("ASSIGN|COMPRADOR|REQUESTED", $"{p.GetProperty("action").GetString()}|{p.GetProperty("roleCode").GetString()}|{p.GetProperty("status").GetString()}");
        Assert.NotEqual(JsonValueKind.Null, p.GetProperty("requestedAt").ValueKind);
        Assert.Equal(
            (RoleChangeErrors.ReasonRequired, AuthorizationErrors.NotAuthorized, RoleChangeErrors.RequestNotPending),
            (noReason.Code, requester.Code, twice.Code)); // the security admin may request, not decide
        var r = Assert.Single(rejected.GetProperty("items").EnumerateArray());
        Assert.Equal("El puesto no requiere compras", r.GetProperty("rejectionReason").GetString());
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.role_assignment WHERE user_id = @u", ("u", a.Target)));
        Assert.Equal("El puesto no requiere compras", await h.ScalarAsync<string>($"SELECT reason FROM core.state_history WHERE aggregate_id = '{request}' AND to_state = 'REJECTED'"));
    }

    [Fact]
    public async Task The_affected_user_cannot_reject_a_request_about_themselves()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var approver = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", a.ApproverSession));
        var request = (await h.RunAsync(new RequestRoleAssignment(h.CompanyId, a.AdminSession, "req", approver, "AUDITOR"), new RequestRoleAssignmentHandler())).ResultRef;

        var self = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RejectRoleChange(h.CompanyId, a.ApproverSession, "rej", request, "No lo quiero"), new RejectRoleChangeHandler()));

        Assert.Equal(RoleChangeErrors.SecondApproverRequired, self.Code);
    }

    [Fact]
    public async Task A_rejection_must_carry_its_rejecter_and_reason_in_the_database()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var request = (await h.RunAsync(new RequestRoleAssignment(h.CompanyId, a.AdminSession, "req", a.Target, "COMPRADOR"), new RequestRoleAssignmentHandler())).ResultRef;

        var bare = await h.AppExecuteAsync($"UPDATE iam.role_assignment_request SET status = 'REJECTED' WHERE request_id = '{request}'");

        Assert.Equal("23514", bare?.SqlState); // role_assignment_request_rejected
    }

    [Fact]
    public async Task Users_and_their_roles_are_listed_for_iam_readers_only()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var request = (await h.RunAsync(new RequestRoleAssignment(h.CompanyId, a.AdminSession, "req", a.Target, "COMPRADOR"), new RequestRoleAssignmentHandler())).ResultRef;
        await h.RunAsync(new ApproveRoleChange(h.CompanyId, a.ApproverSession, "app", request), new ApproveRoleChangeHandler());
        var auditor = await h.SessionWithRolesAsync("AUDITOR");
        var director = await h.SessionWithRolesAsync("DIRECTOR");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var users = JsonDocument.Parse(await h.Queries.ExecuteAsync(new ListUsers(h.CompanyId, auditor), new ListUsersHandler())).RootElement;
        await h.Queries.ExecuteAsync(new ListUsers(h.CompanyId, director), new ListUsersHandler());
        var denied = await Assert.ThrowsAsync<DomainException>(() => h.Queries.ExecuteAsync(new ListUsers(h.CompanyId, buyer), new ListUsersHandler()));

        var target = users.GetProperty("items").EnumerateArray().Single(u => u.GetProperty("userId").GetGuid() == a.Target);
        Assert.Equal("COMPRADOR", Assert.Single(target.GetProperty("roles").EnumerateArray()).GetProperty("roleCode").GetString());
        Assert.Equal(AuthorizationErrors.NotAuthorized, denied.Code);
    }
}
