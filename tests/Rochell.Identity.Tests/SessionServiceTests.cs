using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>Office login from validated Google claims (E-PR03-3, E-PR03-8) and session lifecycle.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SessionServiceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Login_creates_a_session_that_counts_as_re_authentication()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var sessionId = await h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(h.UserId), System.Net.IPAddress.Loopback, "browser");

        Assert.True(await h.ScalarAsync<bool>(
            "SELECT user_id = @u AND auth_method = 'OIDC_GOOGLE' AND last_step_up_at = login_at AND logout_at IS NULL AND ip = '127.0.0.1'::inet FROM iam.session WHERE session_id = @s",
            ("u", h.UserId),
            ("s", sessionId)));
    }

    public static TheoryData<string, OidcClaimsCase> RejectedClaims() => new()
    {
        { "unverified e-mail", OidcClaimsCase.Unverified },
        { "other Google domain", OidcClaimsCase.OtherDomain },
        { "no hosted domain (personal Gmail)", OidcClaimsCase.NoDomain },
        { "unknown subject", OidcClaimsCase.UnknownSubject },
    };

    [Theory]
    [MemberData(nameof(RejectedClaims))]
    public async Task Login_rejects_identities_outside_the_rules(string reason, OidcClaimsCase claimsCase)
    {
        Assert.False(string.IsNullOrEmpty(reason));
        await using var h = await TestHarness.CreateAsync(postgres);
        var valid = TestHarness.ClaimsOf(h.UserId);
        var claims = claimsCase switch
        {
            OidcClaimsCase.Unverified => valid with { EmailVerified = false },
            OidcClaimsCase.OtherDomain => valid with { HostedDomain = "gmail.com" },
            OidcClaimsCase.NoDomain => valid with { HostedDomain = null },
            _ => valid with { Subject = "sub-unknown" },
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(claims, null, null));

        Assert.Equal(SessionService.LoginRejected, ex.Code);
    }

    [Fact]
    public async Task Disabled_users_cannot_log_in()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var disabled = await h.CreateUserAsync(status: "DISABLED");

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(disabled), null, null));

        Assert.Equal(SessionService.LoginRejected, ex.Code);
    }

    [Fact]
    public async Task Step_up_requires_the_same_identity_and_an_open_session()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var other = await h.CreateUserAsync();
        clock.Advance(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<DomainException>(() => h.Sessions.RecordStepUpAsync(h.SessionId, TestHarness.ClaimsOf(other)));
        await h.Sessions.RecordStepUpAsync(h.SessionId, TestHarness.ClaimsOf(h.UserId));

        Assert.Equal(clock.UtcNow, await h.ScalarAsync<DateTime>("SELECT last_step_up_at FROM iam.session WHERE session_id = @s", ("s", h.SessionId)));

        await h.Sessions.EndSessionAsync(h.SessionId);
        await Assert.ThrowsAsync<DomainException>(() => h.Sessions.RecordStepUpAsync(h.SessionId, TestHarness.ClaimsOf(h.UserId)));
    }
}

public enum OidcClaimsCase
{
    Unverified,
    OtherDomain,
    NoDomain,
    UnknownSubject,
}
