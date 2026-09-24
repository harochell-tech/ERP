using Rochell.Identity.RoleChanges;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>T-15 role changes with second approval (Patch 1 P-8), RL-01 and SoD through the real flow.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RoleChangeTests(PostgresFixture postgres)
{
    private sealed record Actors(TestHarness H, Guid Admin, Guid AdminSession, Guid Approver, Guid ApproverSession, Guid Target);

    private async Task<Actors> ActorsAsync(IClockHolder? clock = null)
    {
        var h = await TestHarness.CreateAsync(postgres, clock?.Clock);
        var admin = await h.CreateUserAsync();
        var approver = await h.CreateUserAsync();
        var target = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, admin, "ADMIN_SEGURIDAD");
        await h.GrantAsync(h.CompanyId, approver, "SEGUNDO_APROBADOR_SEGURIDAD");
        return new Actors(h, admin, await h.CreateSessionAsync(admin), approver, await h.CreateSessionAsync(approver), target);
    }

    private static Task<CommandResult> Request(Actors a, Guid target, string role, string key)
        => a.H.RunAsync(new RequestRoleAssignment(a.H.CompanyId, a.AdminSession, key, target, role), new RequestRoleAssignmentHandler());

    private static Task<CommandResult> Approve(Actors a, Guid session, Guid requestId, string key)
        => a.H.RunAsync(new ApproveRoleChange(a.H.CompanyId, session, key, requestId), new ApproveRoleChangeHandler());

    [Fact]
    public async Task T15_assignment_takes_effect_only_after_second_approval()
    {
        var a = await ActorsAsync();
        await using var h = a.H;

        var request = await Request(a, a.Target, "COMPRADOR", "req-1");
        Assert.Equal(0L, await ActiveAsync(h, a.Target, "COMPRADOR"));

        await Approve(a, a.ApproverSession, request.ResultRef, "app-1");

        Assert.Equal(1L, await ActiveAsync(h, a.Target, "COMPRADOR"));
        Assert.Equal("APPROVED", await h.ScalarAsync<string>("SELECT status FROM iam.role_assignment_request WHERE request_id = @r", ("r", request.ResultRef)));
        Assert.Equal(a.Admin, await h.ScalarAsync<Guid>(
            "SELECT granted_by FROM iam.role_assignment ra JOIN iam.role r USING (role_id) WHERE ra.user_id = @u AND r.code = 'COMPRADOR'", ("u", a.Target)));
        Assert.Equal(
            "RoleChangeRequested,RoleChangeApproved,RoleAssigned",
            await h.ScalarAsync<string>(
                "SELECT string_agg(event_type, ',' ORDER BY recorded_at, command_event_index) FROM core.domain_event WHERE aggregate_type IN ('RoleAssignmentRequest', 'RoleAssignment')"));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type IN ('RoleAssignmentRequest', 'RoleAssignment')"));
    }

    [Fact]
    public async Task Requester_cannot_approve_and_affected_user_cannot_approve()
    {
        var a = await ActorsAsync();
        await using var h = a.H;

        var forTarget = await Request(a, a.Target, "COMPRADOR", "req-a");
        var byRequester = await Assert.ThrowsAsync<DomainException>(() => Approve(a, a.AdminSession, forTarget.ResultRef, "app-a"));

        var forApprover = await Request(a, a.Approver, "AUDITOR", "req-b");
        var bySelf = await Assert.ThrowsAsync<DomainException>(() => Approve(a, a.ApproverSession, forApprover.ResultRef, "app-b"));

        Assert.Equal(AuthorizationErrors.NotAuthorized, byRequester.Code); // the requester lacks role:second_approve (SoD)
        Assert.Equal(RoleChangeErrors.SecondApproverRequired, bySelf.Code);
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.role_assignment_request WHERE status = 'REQUESTED'"));
    }

    [Fact]
    public async Task RL01_nobody_can_request_a_role_for_themselves()
    {
        var a = await ActorsAsync();
        await using var h = a.H;

        var ex = await Assert.ThrowsAsync<DomainException>(() => Request(a, a.Admin, "COMPRADOR", "self"));

        Assert.Equal(RoleChangeErrors.SelfRequest, ex.Code);
        Assert.Equal(0L, await h.CountAsync("iam.role_assignment_request"));
    }

    [Fact]
    public async Task SC02_segregation_of_duties_blocks_the_approval()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        await h.GrantAsync(h.CompanyId, a.Target, "ALMACENISTA");
        var request = await Request(a, a.Target, "CUENTAS_POR_PAGAR", "req-sod");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Approve(a, a.ApproverSession, request.ResultRef, "app-sod"));

        Assert.Equal(RoleChangeErrors.SodConflict, ex.Code);
        Assert.Contains("goods_receipt:post", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await ActiveAsync(h, a.Target, "CUENTAS_POR_PAGAR"));
        Assert.Equal("REQUESTED", await h.ScalarAsync<string>("SELECT status FROM iam.role_assignment_request WHERE request_id = @r", ("r", request.ResultRef)));
    }

    [Fact]
    public async Task Revocation_sets_valid_to_after_second_approval()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        await h.GrantAsync(h.CompanyId, a.Target, "COMPRADOR");

        var request = await h.RunAsync(
            new RequestRoleRevocation(h.CompanyId, a.AdminSession, "rev-1", a.Target, "COMPRADOR"),
            new RequestRoleRevocationHandler());
        await Approve(a, a.ApproverSession, request.ResultRef, "rev-app-1");

        Assert.Equal(0L, await ActiveAsync(h, a.Target, "COMPRADOR"));
    }

    [Fact]
    public async Task Request_cannot_be_approved_twice_and_unknown_roles_are_rejected()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var request = await Request(a, a.Target, "COMPRADOR", "req-twice");
        await Approve(a, a.ApproverSession, request.ResultRef, "app-twice-1");

        var twice = await Assert.ThrowsAsync<DomainException>(() => Approve(a, a.ApproverSession, request.ResultRef, "app-twice-2"));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => Request(a, a.Target, "NO_EXISTE", "req-unknown"));

        Assert.Equal(RoleChangeErrors.RequestNotPending, twice.Code);
        Assert.Equal(RoleChangeErrors.RoleUnknown, unknown.Code);
    }

    [Fact]
    public async Task Role_changes_require_recent_re_authentication()
    {
        var holder = new IClockHolder();
        var a = await ActorsAsync(holder);
        await using var h = a.H;
        holder.Clock.Advance(TimeSpan.FromMinutes(6));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Request(a, a.Target, "COMPRADOR", "req-stale"));

        Assert.Equal(AuthorizationErrors.StepUpRequired, ex.Code);
    }

    private static Task<long> ActiveAsync(TestHarness h, Guid user, string role)
        => h.ScalarAsync<long>(
            "SELECT count(*) FROM iam.role_assignment ra JOIN iam.role r USING (role_id) WHERE ra.user_id = @u AND r.code = @r AND ra.valid_to IS NULL",
            ("u", user),
            ("r", role));

    private sealed class IClockHolder
    {
        public FakeClock Clock { get; } = new();
    }
}
