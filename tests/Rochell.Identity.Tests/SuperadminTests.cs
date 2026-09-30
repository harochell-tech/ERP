using Npgsql;
using Rochell.Identity.RoleChanges;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>ADM-2 (E-ADM-2-1…7): the superadministrator's permissions, term, waived controls and their mark.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SuperadminTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Superadmin_holds_every_permission_but_act_as_for_at_most_90_days_company_wide()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var user = await h.CreateUserAsync();

        await h.GrantAsync(h.CompanyId, user, "SUPERADMIN");
        await h.AdminExecuteAsync("INSERT INTO iam.permission (permission_code, access) VALUES ('later:thing', 'WRITE')");

        Assert.Equal(TimeSpan.FromDays(90), await h.ScalarAsync<TimeSpan>(
            "SELECT valid_to - valid_from FROM iam.role_assignment ra JOIN iam.role r USING (role_id) WHERE ra.user_id = @u AND r.code = 'SUPERADMIN'", ("u", user)));
        Assert.Equal(0L, await h.ScalarAsync<long>(
            """
            SELECT count(*) FROM iam.permission p
            WHERE (p.permission_code <> 'identity:act_as')
              <> EXISTS (SELECT 1 FROM iam.role_permission rp JOIN iam.role r USING (role_id) WHERE r.code = 'SUPERADMIN' AND rp.permission_code = p.permission_code)
            """));
        var tooLong = await h.AdminExecuteAsync(
            $"""
            INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, valid_from, valid_to, granted_by)
            SELECT gen_random_uuid(), '{h.CompanyId}', '{await h.CreateUserAsync()}', role_id, now(), now() + interval '91 days', '00000000-0000-7000-8000-00000000d001'
            FROM iam.role WHERE code = 'SUPERADMIN'
            """);
        var plant = await Assert.ThrowsAsync<PostgresException>(async () => await h.GrantAsync(h.CompanyId, await h.CreateUserAsync(), "SUPERADMIN", await h.CreatePlantAsync()));

        Assert.Equal(SqlStates.CheckViolation, tooLong?.SqlState);
        Assert.Contains("not for a plant", plant.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_superadmin_requests_and_approves_alone_the_waiver_is_marked_and_segregation_does_not_apply()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var super = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, super, "SUPERADMIN");
        await h.GrantAsync(h.CompanyId, super, "AUDITOR"); // E-PR03-4 b would refuse it next to any write permission
        var session = await h.CreateSessionAsync(super);
        var target = await h.CreateUserAsync();

        var request = await h.RunAsync(new RequestRoleAssignment(h.CompanyId, session, "req", target, "COMPRADOR"), new RequestRoleAssignmentHandler());
        await h.RunAsync(new ApproveRoleChange(h.CompanyId, session, "app", request.ResultRef), new ApproveRoleChangeHandler());

        Assert.Equal(super, await h.ScalarAsync<Guid>("SELECT second_approved_by FROM iam.role_assignment_request WHERE request_id = @r", ("r", request.ResultRef)));
        Assert.Equal("Identity.ApproveRoleChange:true,Identity.RequestRoleAssignment:false", await h.ScalarAsync<string>(
            "SELECT string_agg(command_type || ':' || controls_waived, ',' ORDER BY command_type) FROM core.command_log WHERE command_type LIKE 'Identity.%'"));
        Assert.Equal("REQUESTED:false,ACTIVE:true,APPROVED:true", await h.ScalarAsync<string>(
            "SELECT string_agg(to_state || ':' || controls_waived, ',' ORDER BY to_state = 'REQUESTED' DESC, to_state) FROM core.state_history WHERE aggregate_type IN ('RoleAssignmentRequest', 'RoleAssignment')"));
    }

    [Fact]
    public async Task Everyone_else_keeps_four_eyes_and_the_database_still_refuses_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var admin = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, admin, "ADMIN_SEGURIDAD");
        var super = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, super, "SUPERADMIN");
        var target = await h.CreateUserAsync();

        string SelfApproved(Guid actor) =>
            $"""
            INSERT INTO iam.role_assignment_request (company_id, request_id, user_id, role_id, action, requested_by, second_approved_by, status)
            SELECT '{h.CompanyId}', gen_random_uuid(), '{target}', role_id, 'ASSIGN', '{actor}', '{actor}', 'APPROVED' FROM iam.role WHERE code = 'COMPRADOR'
            """;
        var refused = await h.AdminExecuteAsync(SelfApproved(admin));
        var waived = await h.AdminExecuteAsync(SelfApproved(super));
        var forSelf = await h.AdminExecuteAsync(
            $"""
            INSERT INTO iam.role_assignment_request (company_id, request_id, user_id, role_id, action, requested_by, second_approved_by, status)
            SELECT '{h.CompanyId}', gen_random_uuid(), '{super}', role_id, 'ASSIGN', '{admin}', '{super}', 'APPROVED' FROM iam.role WHERE code = 'COMPRADOR'
            """);

        Assert.Equal((SqlStates.CheckViolation, "role_assignment_request_distinct_approver"), (refused?.SqlState, refused?.ConstraintName));
        Assert.Null(waived);
        Assert.Equal(SqlStates.CheckViolation, forSelf?.SqlState); // a superadmin still never decides on their own roles
    }

    [Fact]
    public async Task A_superadmin_assignment_is_revoked_before_its_end_and_then_waives_nothing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var admin = await h.SessionWithRolesAsync("ADMIN_SEGURIDAD");
        var approver = await h.SessionWithRolesAsync("SEGUNDO_APROBADOR_SEGURIDAD");
        var super = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, super, "SUPERADMIN");
        var waived = "SELECT iam.controls_waived(@c, @u, now() + interval '1 second')"; // past the app/database clock skew of Docker Desktop

        Assert.True(await h.ScalarAsync<bool>(waived, ("c", h.CompanyId), ("u", super)));
        var again = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RequestRoleAssignment(h.CompanyId, admin, "again", super, "SUPERADMIN"), new RequestRoleAssignmentHandler()));
        var revoke = await h.RunAsync(new RequestRoleRevocation(h.CompanyId, admin, "revoke", super, "SUPERADMIN"), new RequestRoleRevocationHandler());
        await h.RunAsync(new ApproveRoleChange(h.CompanyId, approver, "approve", revoke.ResultRef), new ApproveRoleChangeHandler());

        Assert.Equal(RoleChangeErrors.AlreadyAssigned, again.Code);
        Assert.False(await h.ScalarAsync<bool>(waived, ("c", h.CompanyId), ("u", super)));
        Assert.NotNull(await h.AdminExecuteAsync("UPDATE iam.role_assignment SET valid_to = valid_to + interval '1 hour' WHERE user_id = '" + super + "'")); // never extended
    }
}
