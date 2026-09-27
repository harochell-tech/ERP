using Npgsql;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>
/// E-B03-14 (a): in TEST databases a PROBADOR acts as synthetic users; each is a distinct user, so segregation of duties holds.
/// Outside TEST the database refuses synthetic users, acting sessions and the act-as role.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TestIdentityTests(PostgresFixture postgres)
{
    private static async Task<Guid> SyntheticAsync(TestHarness h, string email)
    {
        var id = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO iam.user (user_id, kind, email, status) VALUES ('{id}', 'SYNTHETIC', '{email}', 'ACTIVE')");
        return id;
    }

    /// <summary>A TEST database with a signed-in tester (PROBADOR) and a synthetic buyer who may ping.</summary>
    private static async Task<(Guid Tester, Guid TesterSession, Guid Buyer)> SetupAsync(TestHarness h)
    {
        await h.InitTestEnvironmentAsync();
        var tester = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, tester, "PROBADOR");
        var buyer = await SyntheticAsync(h, "comprador@staging.invalid");
        await h.GrantAsync(h.CompanyId, buyer, "COMPRADOR");
        await h.GrantAsync(h.CompanyId, buyer, "TEST_PINGER");
        return (tester, await h.CreateSessionAsync(tester), buyer);
    }

    [Fact]
    public async Task Outside_TEST_databases_there_are_no_synthetic_users_nor_act_as_role()
    {
        await using var h = await TestHarness.CreateAsync(postgres); // core.deployment_environment not initialized
        var person = await h.CreateUserAsync();

        var synthetic = await h.AdminExecuteAsync($"INSERT INTO iam.user (user_id, kind, email, status) VALUES ('{Guid.CreateVersion7()}', 'SYNTHETIC', 'x@staging.invalid', 'ACTIVE')");
        var grant = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, person, "PROBADOR"));
        await h.AdminRequireAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('PRODUCTION', current_user, now())");
        var production = await h.AdminExecuteAsync($"INSERT INTO iam.user (user_id, kind, email, status) VALUES ('{Guid.CreateVersion7()}', 'SYNTHETIC', 'y@staging.invalid', 'ACTIVE')");

        Assert.Equal(SqlStates.RaiseException, synthetic?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (grant as PostgresException)?.SqlState);
        Assert.Equal(SqlStates.RaiseException, production?.SqlState);
    }

    [Fact]
    public async Task A_tester_acts_as_a_synthetic_user_whose_commands_are_its_own()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (tester, session, buyer) = await SetupAsync(h);

        var identities = await h.Sessions.ListTestIdentitiesAsync(session, h.CompanyId);
        var acting = await h.Sessions.ActAsAsync(session, h.CompanyId, buyer);
        var result = await h.RunAsync(new PingCommand(h.CompanyId, acting, "as-buyer", "hola"), new PingHandler());
        var described = await h.Sessions.DescribeAsync(acting);

        var identity = Assert.Single(identities);
        Assert.Equal(("comprador@staging.invalid", "COMPRADOR,TEST_PINGER"), (identity.Email, string.Join(',', identity.Roles)));
        Assert.False(result.Duplicate);
        Assert.Equal(buyer, described.UserId);
        Assert.Equal("comprador@staging.invalid", described.Email);
        Assert.Equal($"{tester:N}@{TestHarness.HostedDomain}", described.AuthenticatedEmail);
        // The trail names both: the command's session is the acting one, which points to the tester's Google session.
        Assert.Equal($"{buyer}|{session}|{tester}", await h.ScalarAsync<string>(
            """
            SELECT s.user_id || '|' || s.authenticated_session_id || '|' || p.user_id
            FROM core.command_log c JOIN iam.session s ON s.session_id = c.session_id JOIN iam.session p ON p.session_id = s.authenticated_session_id
            WHERE c.idempotency_key = 'as-buyer'
            """));
    }

    [Fact]
    public async Task Acting_needs_act_as_and_a_synthetic_user_with_roles_in_that_company()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (_, session, buyer) = await SetupAsync(h);
        var plain = await h.CreateSessionAsync(await h.CreateUserAsync());
        var roleless = await SyntheticAsync(h, "sinrol@staging.invalid");
        var person = await h.CreateUserAsync();

        var withoutPermission = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.ActAsAsync(plain, h.CompanyId, buyer));
        var withoutRoles = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.ActAsAsync(session, h.CompanyId, roleless));
        var aPerson = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.ActAsAsync(session, h.CompanyId, person));
        var elsewhere = await h.CreateCompanyAsync();
        var otherCompany = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.ActAsAsync(session, elsewhere, buyer));

        Assert.Equal(AuthorizationErrors.NotAuthorized, withoutPermission.Code);
        Assert.Equal(SessionService.TestIdentityUnavailable, withoutRoles.Code);
        Assert.Equal(SessionService.TestIdentityUnavailable, aPerson.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, otherCompany.Code);
    }

    [Fact]
    public async Task The_database_refuses_acting_sessions_the_service_would_not_open()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (_, session, buyer) = await SetupAsync(h);
        var plainSession = await h.CreateSessionAsync(await h.CreateUserAsync());
        string Insert(Guid parent, string method = "ACT_AS", Guid? user = null)
            => $"""
               INSERT INTO iam.session (session_id, user_id, auth_method, login_at, last_activity_at, authenticated_session_id)
               VALUES (gen_random_uuid(), '{user ?? buyer}', '{method}', now(), now(), {(method == "ACT_AS" ? $"'{parent}'" : "NULL")})
               """;

        var noPermission = await h.AppExecuteAsync(Insert(plainSession));
        var syntheticViaGoogle = await h.AppExecuteAsync(Insert(session, "OIDC_GOOGLE"));
        var ok = await h.AppExecuteAsync(Insert(session));

        Assert.Equal(SqlStates.RaiseException, noPermission?.SqlState);
        Assert.Equal(SqlStates.RaiseException, syntheticViaGoogle?.SqlState);
        Assert.Null(ok);
    }

    [Fact]
    public async Task Segregation_of_duties_binds_synthetic_users_like_anyone()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await SetupAsync(h);
        var warehouse = await SyntheticAsync(h, "almacen@staging.invalid");
        await h.GrantAsync(h.CompanyId, warehouse, "ALMACENISTA");

        var conflict = await Assert.ThrowsAsync<PostgresException>(() => h.GrantAsync(h.CompanyId, warehouse, "CUENTAS_POR_PAGAR"));

        Assert.StartsWith("SOD_CONFLICT", conflict.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_returning_and_signing_out_close_the_right_sessions()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (_, session, buyer) = await SetupAsync(h);
        var approver = await SyntheticAsync(h, "aprobador@staging.invalid");
        await h.GrantAsync(h.CompanyId, approver, "APROBADOR_COMPRAS");

        var asBuyer = await h.Sessions.ActAsAsync(session, h.CompanyId, buyer);
        var asApprover = await h.Sessions.ActAsAsync(asBuyer, h.CompanyId, approver); // switch directly
        var back = await h.Sessions.StopActingAsync(asApprover);
        var again = await h.Sessions.ActAsAsync(back, h.CompanyId, buyer);
        await h.Sessions.EndSessionAsync(again);                                       // sign out while acting

        Assert.Equal(session, back);
        var closed = await h.ScalarAsync<string>(
            $"SELECT string_agg(CASE WHEN logout_at IS NULL THEN 'open' ELSE 'closed' END, ',' ORDER BY login_at, session_id) FROM iam.session WHERE session_id IN ('{session}', '{asBuyer}', '{asApprover}', '{again}')");
        Assert.Equal("closed,closed,closed,closed", closed);
        await Assert.ThrowsAsync<DomainException>(() => h.Sessions.DescribeAsync(again));
    }

    [Fact]
    public async Task An_acting_session_ends_with_the_signed_in_session_and_steps_up_with_the_person()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (tester, session, buyer) = await SetupAsync(h);
        var stranger = await h.CreateUserAsync();
        var acting = await h.Sessions.ActAsAsync(session, h.CompanyId, buyer);

        var noStepUpYet = (await h.Sessions.DescribeAsync(acting)).StepUpValidUntil;
        var byStranger = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.RecordStepUpAsync(acting, TestHarness.ClaimsOf(stranger)));
        await h.Sessions.RecordStepUpAsync(acting, TestHarness.ClaimsOf(tester));
        var stepped = (await h.Sessions.DescribeAsync(acting)).StepUpValidUntil;
        await h.AdminRequireAsync($"UPDATE iam.session SET logout_at = now() WHERE session_id = '{session}'");
        var afterParentLogout = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PingCommand(h.CompanyId, acting, "late", "hola"), new PingHandler()));

        Assert.Null(noStepUpYet);
        Assert.Equal(SessionService.LoginRejected, byStranger.Code);
        Assert.NotNull(stepped);
        Assert.Equal(AuthorizationErrors.SessionInvalid, afterParentLogout.Code);
    }
}
