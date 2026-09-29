using Npgsql;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>E-FIS1-04-7: the daily process's identity, its only role and its SERVICE sessions.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ServiceSessionTests(PostgresFixture postgres)
{
    private static readonly Guid Daily = IdentityConstants.DailyProcessUserId;

    [Fact]
    public async Task PROCESO_DIARIO_is_only_for_service_identities_and_they_hold_nothing_else()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var person = await h.CreateUserAsync();

        var toPerson = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, person, "PROCESO_DIARIO"));
        var otherRole = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, Daily, "CONTROLLER"));
        await h.GrantAsync(h.CompanyId, Daily, "PROCESO_DIARIO");

        Assert.Equal(SqlStates.RaiseException, (toPerson as PostgresException)?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (otherRole as PostgresException)?.SqlState);
        Assert.Equal("fiscal_authorization:suspend", await h.ScalarAsync<string>(
            "SELECT string_agg(permission_code, ',') FROM iam.role r JOIN iam.role_permission USING (role_id) WHERE r.code = 'PROCESO_DIARIO'"));
    }

    [Fact]
    public async Task A_SERVICE_session_is_only_of_a_service_identity_and_never_passes_the_cookie_path()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var person = await h.CreateUserAsync();

        var serviceForPerson = await h.AdminExecuteAsync(
            $"INSERT INTO iam.session (session_id, user_id, auth_method, login_at, last_activity_at) VALUES ('{Guid.CreateVersion7()}', '{person}', 'SERVICE', now(), now())");
        var googleForService = await h.AdminExecuteAsync(
            $"INSERT INTO iam.session (session_id, user_id, auth_method, login_at, last_activity_at) VALUES ('{Guid.CreateVersion7()}', '{Daily}', 'OIDC_GOOGLE', now(), now())");
        var startForPerson = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartServiceSessionAsync(person));
        var session = await h.Sessions.StartServiceSessionAsync(Daily);
        var described = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.DescribeAsync(session));
        await h.Sessions.EndSessionAsync(session);

        Assert.Equal(SqlStates.RaiseException, serviceForPerson?.SqlState);
        Assert.Equal(SqlStates.RaiseException, googleForService?.SqlState);
        Assert.Equal((AuthorizationErrors.SessionInvalid, AuthorizationErrors.SessionInvalid), (startForPerson.Code, described.Code));
        Assert.Equal("SERVICE:true", await h.ScalarAsync<string>(
            "SELECT auth_method || ':' || (logout_at IS NOT NULL)::text FROM iam.session WHERE session_id = @s", ("s", session)));
    }
}
