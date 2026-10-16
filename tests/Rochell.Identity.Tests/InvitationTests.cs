using System.Text.Json;
using Rochell.Identity.Invitations;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>
/// SEC-02 (E-SEC-02-1…8): an invitation is requested by one person and approved by another; its exact verified e-mail enters with Google
/// the first time and becomes a user with the invited roles; guests come from outside the domain, never hold security roles and their
/// roles end; an expired or cancelled invitation does not enter; a user is disabled and enabled with a second approver.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InvitationTests(PostgresFixture postgres)
{
    private sealed record Actors(TestHarness H, Guid Admin, Guid AdminSession, Guid ApproverSession);

    private async Task<Actors> ActorsAsync(FakeClock? clock = null)
    {
        var h = await TestHarness.CreateAsync(postgres, clock);
        var admin = await h.CreateUserAsync();
        var approver = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, admin, "ADMIN_SEGURIDAD");
        await h.GrantAsync(h.CompanyId, approver, "SEGUNDO_APROBADOR_SEGURIDAD");
        return new Actors(h, admin, await h.CreateSessionAsync(admin), await h.CreateSessionAsync(approver));
    }

    private static Task<CommandResult> Invite(Actors a, string key, string email, params InvitedRole[] roles)
        => a.H.RunAsync(new InviteUser(a.H.CompanyId, a.AdminSession, key, email, "Persona Invitada", roles), new InviteUserHandler(a.H.Options));

    private static Task<CommandResult> Approve(Actors a, Guid invitation, string key, Guid? session = null)
        => a.H.RunAsync(new ApproveInvitation(a.H.CompanyId, session ?? a.ApproverSession, key, invitation), new ApproveInvitationHandler(a.H.Options));

    private static OidcClaims Google(string subject, string email, string? domain) => new(subject, email, EmailVerified: true, domain, "Nombre de Google");

    [Fact]
    public async Task A_company_invitation_enters_once_with_its_exact_email_and_gets_its_roles()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var invitation = (await Invite(a, "inv", "Ana@Rochell.com.do", new InvitedRole("COMPRADOR"))).ResultRef;

        // Before the approval nothing enters; the requester (security administrator) holds no second approval.
        var early = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(Google("sub-ana", "ana@rochell.com.do", TestHarness.HostedDomain), null, null));
        var self = await Assert.ThrowsAsync<DomainException>(() => Approve(a, invitation, "self", a.AdminSession));
        var approved = JsonDocument.Parse((await Approve(a, invitation, "ok")).ResultPayload).RootElement;

        var session = await h.Sessions.StartOidcSessionAsync(Google("sub-ana", "ana@rochell.com.do", TestHarness.HostedDomain), null, "tests");
        var again = await h.Sessions.StartOidcSessionAsync(Google("sub-ana", "ana@rochell.com.do", TestHarness.HostedDomain), null, "tests");
        var otherAccount = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(Google("sub-impostor", "ana@rochell.com.do", TestHarness.HostedDomain), null, null));

        Assert.Equal((SessionService.LoginRejected, "NOT_AUTHORIZED", "APPROVED"), (early.Code, self.Code, approved.GetProperty("status").GetString()));
        Assert.NotEqual(session, again);
        Assert.Equal(SessionService.LoginRejected, otherAccount.Code);
        Assert.Equal("HUMAN:ana@rochell.com.do:Nombre de Google:COMPRADOR:ACCEPTED:true", await h.ScalarAsync<string>(
            """
            SELECT u.kind || ':' || u.email || ':' || u.display_name || ':' || r.code || ':' || i.status || ':' || (ra.valid_to IS NULL)
            FROM iam.user u JOIN iam.role_assignment ra ON ra.user_id = u.user_id JOIN iam.role r ON r.role_id = ra.role_id
            JOIN iam.invitation i ON i.accepted_user_id = u.user_id
            WHERE u.oidc_subject = 'sub-ana'
            """));
        Assert.Equal("INVITATION:ana@rochell.com.do", await h.ScalarAsync<string>("SELECT document_type || ':' || array_to_string(recipients, ',') FROM core.mail_message"));
    }

    [Fact]
    public async Task A_guest_comes_from_outside_the_domain_never_holds_security_roles_and_its_roles_end()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var security = await Assert.ThrowsAsync<DomainException>(() => Invite(a, "bad", "auditor@gmail.com", new InvitedRole("ADMIN_SEGURIDAD")));
        var invitation = JsonDocument.Parse((await Invite(a, "inv", "contador@gmail.com", new InvitedRole("CONTADOR"))).ResultPayload).RootElement;
        await Approve(a, invitation.GetProperty("invitationId").GetGuid(), "ok");

        // A guest invitation does not enter through a company account, nor a company invitation through an outside one.
        var wrongKind = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(Google("sub-c", "contador@gmail.com", TestHarness.HostedDomain), null, null));
        await h.Sessions.StartOidcSessionAsync(Google("sub-c", "contador@gmail.com", null), null, "tests");
        var guest = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.user WHERE oidc_subject = 'sub-c'");

        Assert.Equal((InvitationErrors.RoleNotForGuest, true, SessionService.LoginRejected), (security.Code, invitation.GetProperty("guest").GetBoolean(), wrongKind.Code));
        Assert.Equal("GUEST:true:true", await h.ScalarAsync<string>(
            """
            SELECT u.kind || ':' || (u.employee_id IS NULL) || ':' || (ra.valid_to BETWEEN now() + interval '89 days' AND now() + interval '92 days')
            FROM iam.user u JOIN iam.role_assignment ra ON ra.user_id = u.user_id WHERE u.user_id = @u
            """,
            ("u", guest)));
        Assert.True(await h.Sessions.IsGuestSessionAsync(await h.ScalarAsync<Guid>("SELECT session_id FROM iam.session WHERE user_id = @u", ("u", guest))));
    }

    [Fact]
    public async Task An_expired_or_cancelled_invitation_does_not_enter()
    {
        var clock = new FakeClock();
        var a = await ActorsAsync(clock);
        await using var h = a.H;
        var late = (await Invite(a, "late", "tarde@rochell.com.do", new InvitedRole("COMPRADOR"))).ResultRef;
        await Approve(a, late, "ok-late");
        var cancelled = (await Invite(a, "cancel", "anulada@rochell.com.do", new InvitedRole("COMPRADOR"))).ResultRef;
        await Approve(a, cancelled, "ok-cancel");
        await h.RunAsync(new CancelInvitation(h.CompanyId, a.AdminSession, "c", cancelled, "Ya no trabaja con nosotros"), new CancelInvitationHandler());

        clock.Advance(TimeSpan.FromDays(h.Options.InvitationDays + 1));
        var expired = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(Google("sub-t", "tarde@rochell.com.do", TestHarness.HostedDomain), null, null));
        var annulled = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(Google("sub-x", "anulada@rochell.com.do", TestHarness.HostedDomain), null, null));

        Assert.Equal((SessionService.LoginRejected, SessionService.LoginRejected), (expired.Code, annulled.Code));
        Assert.Equal("CANCELLED|EXPIRED", string.Join('|', JsonDocument.Parse(await h.QueryAsync(new ListInvitations(h.CompanyId, await h.CreateSessionAsync(a.Admin)), new ListInvitationsHandler()))
            .RootElement.GetProperty("items").EnumerateArray().OrderBy(i => i.GetProperty("email").GetString(), StringComparer.Ordinal).Select(i => i.GetProperty("state").GetString())));
    }

    [Fact]
    public async Task A_user_is_disabled_and_enabled_with_a_second_approver()
    {
        var a = await ActorsAsync();
        await using var h = a.H;
        var person = await h.CreateUserAsync();
        await h.CreateSessionAsync(person);

        var disable = (await h.RunAsync(new RequestUserStatusChange(h.CompanyId, a.AdminSession, "d", person, "DISABLE", "Salió de la empresa"), new RequestUserStatusChangeHandler())).ResultRef;
        var stillIn = await h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(person), null, null);
        await h.RunAsync(new ApproveUserStatusChange(h.CompanyId, a.ApproverSession, "d-ok", disable), new ApproveUserStatusChangeHandler());
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(person), null, null));
        var described = await Record.ExceptionAsync(() => h.Sessions.DescribeAsync(stillIn));

        var enable = (await h.RunAsync(new RequestUserStatusChange(h.CompanyId, a.AdminSession, "e", person, "ENABLE", "Regresó"), new RequestUserStatusChangeHandler())).ResultRef;
        await h.RunAsync(new ApproveUserStatusChange(h.CompanyId, a.ApproverSession, "e-ok", enable), new ApproveUserStatusChangeHandler());

        Assert.Equal(SessionService.LoginRejected, refused.Code);
        Assert.NotNull(described);
        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status FROM iam.user WHERE user_id = @u", ("u", person)));
        await h.Sessions.StartOidcSessionAsync(TestHarness.ClaimsOf(person), null, null);
    }
}
