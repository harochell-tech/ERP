using Rochell.Platform.Commands;
using Rochell.Platform.Observability;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>Authorization inside the command pipeline: session, permission, company/plant scope, expiry and step-up.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Authorized_command_runs()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var result = await h.RunAsync(h.Ping("ok"), new PingHandler());

        Assert.False(result.Duplicate);
    }

    [Fact]
    public async Task User_without_the_permission_is_rejected_and_nothing_is_written()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var stranger = await h.CreateUserAsync();
        var session = await h.CreateSessionAsync(stranger);

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("no-perm") with { SessionId = session }, new PingHandler()));

        Assert.Equal(AuthorizationErrors.NotAuthorized, ex.Code);
        Assert.Equal((0L, 0L, 0L), await h.CountsAsync());
        Assert.Equal([RequestOutcome.RejectedDomain], await h.OutcomesAsync());
    }

    [Fact]
    public async Task Permission_in_another_company_does_not_authorize()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var otherCompany = await h.CreateCompanyAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("other-co") with { CompanyId = otherCompany }, new PingHandler()));

        Assert.Equal(AuthorizationErrors.NotAuthorized, ex.Code);
    }

    [Fact]
    public async Task Revoked_assignment_no_longer_authorizes()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        Assert.Null(await h.AdminExecuteAsync($"UPDATE iam.role_assignment SET valid_to = now() WHERE user_id = '{h.UserId}'"));

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("revoked"), new PingHandler()));

        Assert.Equal(AuthorizationErrors.NotAuthorized, ex.Code);
    }

    [Fact]
    public async Task Unknown_or_closed_sessions_are_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var unknown = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("unknown") with { SessionId = Guid.CreateVersion7() }, new PingHandler()));
        await h.Sessions.EndSessionAsync(h.SessionId);
        var closed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("closed"), new PingHandler()));

        Assert.Equal(AuthorizationErrors.SessionInvalid, unknown.Code);
        Assert.Equal(AuthorizationErrors.SessionInvalid, closed.Code);
    }

    [Fact]
    public async Task Disabled_user_sessions_stop_working()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        Assert.Null(await h.AdminExecuteAsync($"UPDATE iam.user SET status = 'DISABLED' WHERE user_id = '{h.UserId}'"));

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("disabled"), new PingHandler()));

        Assert.Equal(AuthorizationErrors.SessionInvalid, ex.Code);
    }

    [Fact]
    public async Task Absolute_lifetime_expires_the_session()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        for (var i = 0; i < 24; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(29)); // 24 × 29 min = 11 h 36 min, never idle: every command refreshes activity
            await h.RunAsync(h.Ping($"active-{i}"), new PingHandler());
        }

        clock.Advance(TimeSpan.FromMinutes(29)); // 12 h 05 min since login, only 29 min idle
        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("after-12h"), new PingHandler()));

        Assert.Equal(AuthorizationErrors.SessionExpired, ex.Code);
    }

    [Fact]
    public async Task Idle_timeout_expires_the_session()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        clock.Advance(TimeSpan.FromMinutes(31));

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("idle"), new PingHandler()));

        Assert.Equal(AuthorizationErrors.SessionExpired, ex.Code);
    }

    [Trait("Acceptance", "SC-03")]
    [Fact]
    public async Task Step_up_is_required_after_five_minutes_and_restored_by_re_authentication()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);

        await h.RunAsync(h.Ping("fresh-login"), new StepUpPingHandler());
        clock.Advance(TimeSpan.FromMinutes(6));
        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("stale"), new StepUpPingHandler()));
        await h.Sessions.RecordStepUpAsync(h.SessionId, TestHarness.ClaimsOf(h.UserId));
        var after = await h.RunAsync(h.Ping("stale"), new StepUpPingHandler());

        Assert.Equal(AuthorizationErrors.StepUpRequired, ex.Code);
        Assert.False(after.Duplicate);
    }

    [Fact]
    public async Task Plant_scoped_assignment_authorizes_only_its_plant_and_not_company_wide_commands()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plantA = await h.CreatePlantAsync();
        var plantB = await h.CreatePlantAsync();
        var plantUser = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, plantUser, "TEST_PINGER", plantA);
        var session = await h.CreateSessionAsync(plantUser);

        await h.RunAsync(new PlantPingCommand(h.CompanyId, session, "plant-a", plantA), new PlantPingHandler());
        var otherPlant = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PlantPingCommand(h.CompanyId, session, "plant-b", plantB), new PlantPingHandler()));
        var companyWide = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(h.Ping("company-wide") with { SessionId = session }, new PingHandler()));

        Assert.Equal(AuthorizationErrors.NotAuthorized, otherPlant.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, companyWide.Code);
    }

    [Fact]
    public async Task Company_wide_assignment_authorizes_every_plant()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var result = await h.RunAsync(new PlantPingCommand(h.CompanyId, h.SessionId, "any-plant", await h.CreatePlantAsync()), new PlantPingHandler());

        Assert.False(result.Duplicate);
    }
}
