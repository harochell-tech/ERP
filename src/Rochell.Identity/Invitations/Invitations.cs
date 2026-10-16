using System.Globalization;
using System.Net;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Mail;

namespace Rochell.Identity.Invitations;

// SEC-02 (E-SEC-3, E-SEC-02-1…8): invitations — someone is invited by e-mail with roles, a second person approves, the person enters
// with Google the first time with that exact verified e-mail; and disabling / enabling a user, also with a second approver.

/// <summary>One role of an invitation, for the whole company or one plant.</summary>
public sealed record InvitedRole(string RoleCode, Guid? PlantId = null);

/// <summary>
/// E-SEC-02-1/5: invites <paramref name="Email"/>. An address outside the company's Google domain is a guest: never a superadministrator
/// nor a security role, and its roles end on <paramref name="GuestRolesUntil"/> (by default the configured number of days).
/// </summary>
public sealed record InviteUser(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string Email, string DisplayName, IReadOnlyList<InvitedRole> Roles, DateOnly? GuestRolesUntil = null) : ICommand;

public sealed record ApproveInvitation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvitationId) : ICommand;

public sealed record RejectInvitation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvitationId, string Reason) : ICommand;

public sealed record CancelInvitation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvitationId, string Reason) : ICommand;

/// <summary>E-SEC-02-7: <paramref name="Action"/> DISABLE closes the user's access at once (sessions included); ENABLE gives it back.</summary>
public sealed record RequestUserStatusChange(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid UserId, string Action, string Reason) : ICommand;

public sealed record ApproveUserStatusChange(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RequestId) : ICommand;

public sealed record RejectUserStatusChange(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RequestId, string Reason) : ICommand;

public static class InvitationErrors
{
    public const string EmailInvalid = "INVITATION_EMAIL_INVALID";
    public const string NameRequired = "INVITATION_NAME_REQUIRED";
    public const string RolesRequired = "INVITATION_ROLES_REQUIRED";
    public const string UserExists = "INVITATION_USER_EXISTS";
    public const string AlreadyOpen = "INVITATION_ALREADY_OPEN";
    public const string RoleNotForGuest = "ROLE_NOT_FOR_GUEST";
    public const string GuestEndInvalid = "GUEST_ROLES_END_INVALID";
    public const string NotFound = "INVITATION_NOT_FOUND";
    public const string NotPending = "INVITATION_NOT_PENDING";
    public const string SecondApproverRequired = "SECOND_APPROVER_REQUIRED";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string StatusRequestNotFound = "USER_STATUS_REQUEST_NOT_FOUND";
    public const string StatusUnchanged = "USER_STATUS_UNCHANGED";
    public const string StatusRequestOpen = "USER_STATUS_REQUEST_OPEN";
}

internal static class InvitationSql
{
    public const string Aggregate = "Invitation";
    public const string StatusAggregate = "UserStatusRequest";

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @id", ("id", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static string Reason(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.Length is > 0 and <= 500 // type-limit: iam.invitation decision_reason / user_status_request reason
            ? text
            : throw new DomainException(InvitationErrors.ReasonRequired, "A reason (up to 500 characters) is required.");
    }

    /// <summary>E-SEC-02-5: a role a guest may never hold — SUPERADMIN, or any role carrying a security permission.</summary>
    public static async Task<bool> SecurityRoleAsync(CommandContext context, Guid roleId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT r.code = 'SUPERADMIN' OR EXISTS (
              SELECT 1 FROM iam.role_permission rp JOIN iam.permission p ON p.permission_code = rp.permission_code
              WHERE rp.role_id = r.role_id AND p.access = 'SECURITY')
            FROM iam.role r WHERE r.role_id = @r
            """,
            ("r", roleId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public static async Task<InvitationRow> LockAsync(CommandContext context, Guid invitationId, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT invitation_id, email, display_name, guest, roles_valid_to, status, requested_by, version
                FROM iam.invitation WHERE company_id = @c AND invitation_id = @id FOR UPDATE
                """,
                r => new InvitationRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.NullableUtc(4), r.GetString(5), r.GetGuid(6), r.GetInt64(7)),
                cancellationToken,
                ("c", context.CompanyId),
                ("id", invitationId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(InvitationErrors.NotFound, "The invitation does not exist.");

    public static async Task<Guid> TransitionAsync(
        CommandContext context, InvitationRow row, string to, string commandType, string eventType, object payload, string set, CancellationToken cancellationToken,
        string? reason = null, params (string Name, object? Value)[] parameters)
    {
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.Id, row.Version + 1, JsonSerializer.Serialize(payload), Publish: true), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE iam.invitation SET status = @to, version = version + 1" + set + " WHERE invitation_id = @id",
            cancellationToken,
            [("to", to), ("id", row.Id), .. parameters]).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return eventId;
    }

    public sealed record InvitationRow(Guid Id, string Email, string DisplayName, bool Guest, DateTime? RolesValidTo, string Status, Guid RequestedBy, long Version);
}

[RequiresPermission("role:assign", StepUp = true)]
public sealed class InviteUserHandler(IdentityOptions options) : ICommandHandler<InviteUser>
{
    public string CommandType => "Identity.InviteUser";

    public async Task<string> HandleAsync(InviteUser command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var email = (command.Email ?? string.Empty).Trim().ToLowerInvariant();
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (email.Length > 254 || at <= 0 || at != email.LastIndexOf('@') || email.IndexOf('.', at) < at + 2 || email.EndsWith('.') || email.Any(char.IsWhiteSpace)) // type-limit: e-mail length
        {
            throw new DomainException(InvitationErrors.EmailInvalid, "The e-mail address is not valid.");
        }

        var name = (command.DisplayName ?? string.Empty).Trim();
        if (name.Length is 0 or > 200) // type-limit: iam.invitation display_name
        {
            throw new DomainException(InvitationErrors.NameRequired, "The person's name is required (up to 200 characters).");
        }

        var roles = (command.Roles ?? []).Where(r => r is not null).ToList();
        if (roles.Count == 0 || roles.Select(r => (r.RoleCode, r.PlantId)).Distinct().Count() != roles.Count)
        {
            throw new DomainException(InvitationErrors.RolesRequired, "At least one role, each once.");
        }

        var guest = !string.Equals(email[(at + 1)..], options.HostedDomain, StringComparison.OrdinalIgnoreCase);
        var today = Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        DateTime? rolesValidTo = null;
        if (guest)
        {
            var until = command.GuestRolesUntil ?? today.AddDays(options.GuestRoleDays);
            if (until <= today || until > today.AddDays(options.GuestRoleMaxDays))
            {
                throw new DomainException(InvitationErrors.GuestEndInvalid, $"A guest's roles end after today and within {options.GuestRoleMaxDays} days.");
            }

            rolesValidTo = Platform.Time.BusinessCalendar.DayUtcRange(until).EndUtc;
        }

        await using (var existing = Sql.Command(context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM iam.user WHERE email = @e)", ("e", email)))
        {
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new DomainException(InvitationErrors.UserExists, "A user with that e-mail already exists: give the roles from Usuarios y roles.");
            }
        }

        await using (var open = Sql.Command(
            context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM iam.invitation WHERE company_id = @c AND email = @e AND status IN ('REQUESTED', 'APPROVED'))",
            ("c", context.CompanyId), ("e", email)))
        {
            if (await open.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new DomainException(InvitationErrors.AlreadyOpen, "That e-mail already has an invitation in progress.");
            }
        }

        var resolved = new List<(Guid RoleId, string RoleCode, Guid? PlantId)>();
        foreach (var role in roles)
        {
            Guid roleId;
            await using (var find = Sql.Command(context.Connection, context.Transaction, "SELECT role_id FROM iam.role WHERE code = @code", ("code", role.RoleCode)))
            {
                roleId = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid found
                    ? found
                    : throw new DomainException(RoleChanges.RoleChangeErrors.RoleUnknown, $"Role '{role.RoleCode}' does not exist.");
            }

            if (guest && await InvitationSql.SecurityRoleAsync(context, roleId, cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(InvitationErrors.RoleNotForGuest, $"A guest from outside the company cannot hold {role.RoleCode} (E-SEC-02-5).");
            }

            resolved.Add((roleId, role.RoleCode, role.PlantId));
        }

        var requester = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var id = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "UserInvited",
                1,
                InvitationSql.Aggregate,
                id,
                1,
                JsonSerializer.Serialize(new { invitationId = id, email, displayName = name, guest, rolesValidTo, roles = resolved.Select(r => new { roleCode = r.RoleCode, plantId = r.PlantId }) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO iam.invitation (company_id, invitation_id, email, display_name, guest, roles_valid_to, status, requested_by, requested_at, version)
            VALUES (@c, @id, @e, @n, @g, @until, 'REQUESTED', @by, @now, 1)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id),
            ("e", email),
            ("n", name),
            ("g", guest),
            ("until", rolesValidTo),
            ("by", requester),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        foreach (var (roleId, _, plantId) in resolved)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO iam.invitation_role (company_id, invitation_id, role_id, plant_id) VALUES (@c, @id, @r, @p)",
                cancellationToken,
                ("c", context.CompanyId),
                ("id", id),
                ("r", roleId),
                ("p", plantId)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(InvitationSql.Aggregate, id, "DOCUMENT", null, "REQUESTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invitationId = id, status = "REQUESTED", guest });
    }
}

/// <summary>
/// E-SEC-02-1/6: a second person approves; the invitation is valid for the configured days from now and Core e-mails the invitee the
/// address of Core and the steps (MAIL modes apply: Off / Redirect / Live).
/// </summary>
[RequiresPermission("role:second_approve", StepUp = true)]
public sealed class ApproveInvitationHandler(IdentityOptions options) : ICommandHandler<ApproveInvitation>
{
    public string CommandType => "Identity.ApproveInvitation";

    public async Task<string> HandleAsync(ApproveInvitation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await InvitationSql.LockAsync(context, command.InvitationId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "REQUESTED")
        {
            throw new DomainException(InvitationErrors.NotPending, $"The invitation is {row.Status}.");
        }

        var approver = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.RequestedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(InvitationErrors.SecondApproverRequired, "The invitation is approved by someone other than who requested it.");
        }

        var now = context.Clock.UtcNow;
        var expires = now.AddDays(options.InvitationDays);
        var eventId = await InvitationSql.TransitionAsync(
            context, row, "APPROVED", CommandType, "InvitationApproved", new { invitationId = row.Id, approvedBy = approver, expiresAt = expires },
            ", second_approved_by = @by, approved_at = @now, expires_at = @exp", cancellationToken, null, ("by", approver), ("now", now), ("exp", expires)).ConfigureAwait(false);

        var company = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT legal_name FROM md.company WHERE company_id = @c", r => r.GetString(0), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var legal = company.FirstOrDefault() ?? string.Empty;
        var url = string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? "Rochell Core" : options.PublicBaseUrl!.TrimEnd('/');
        var until = expires.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var button = row.Guest ? "«Entrar con otra cuenta de Google (invitados)»" : "«Entrar con Google»";
        var body =
            $"Hola {row.DisplayName}:\n\n{legal} le invitó a Rochell Core, su sistema de operaciones.\n\n" +
            $"1. Entre a {url}\n2. Pulse {button} y use su cuenta de Google {row.Email}.\n" +
            (row.Guest ? "3. Su cuenta de Google debe tener activada la verificación en dos pasos (myaccount.google.com › Seguridad).\n" : string.Empty) +
            $"\nLa invitación vence el {until}. Si no la esperaba, ignore este correo.";
        var html =
            $"<h1>Invitación a Rochell Core</h1><p>Hola {WebUtility.HtmlEncode(row.DisplayName)}:</p><p>{WebUtility.HtmlEncode(legal)} le invitó a Rochell Core.</p>" +
            $"<ol><li>Entre a {WebUtility.HtmlEncode(url)}</li><li>Pulse {WebUtility.HtmlEncode(button)} y use su cuenta de Google {WebUtility.HtmlEncode(row.Email)}.</li>" +
            (row.Guest ? "<li>Su cuenta de Google debe tener activada la verificación en dos pasos.</li>" : string.Empty) +
            $"</ol><p>La invitación vence el {until}.</p>";
        await MailOutbox.EnqueueAsync(
            context,
            context.Ids.NewId(),
            new MailDraft("INVITATION", row.Id, row.Email, null, [row.Email], "Invitación a Rochell Core", body, "invitacion-rochell-core.pdf", html),
            eventId,
            approver,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invitationId = row.Id, status = "APPROVED", expiresAt = expires });
    }
}

[RequiresPermission("role:second_approve", StepUp = true)]
public sealed class RejectInvitationHandler : ICommandHandler<RejectInvitation>
{
    public string CommandType => "Identity.RejectInvitation";

    public async Task<string> HandleAsync(RejectInvitation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = InvitationSql.Reason(command.Reason);
        var row = await InvitationSql.LockAsync(context, command.InvitationId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "REQUESTED")
        {
            throw new DomainException(InvitationErrors.NotPending, $"The invitation is {row.Status}.");
        }

        var by = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await InvitationSql.TransitionAsync(
            context, row, "REJECTED", CommandType, "InvitationRejected", new { invitationId = row.Id, reason }, ", rejected_by = @by, decided_at = @now, decision_reason = @why", cancellationToken, reason,
            ("by", by), ("now", context.Clock.UtcNow), ("why", reason)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invitationId = row.Id, status = "REJECTED" });
    }
}

/// <summary>E-SEC-02-6: an invitation not yet used is cancelled; its e-mail no longer enters.</summary>
[RequiresPermission("role:assign", StepUp = true)]
public sealed class CancelInvitationHandler : ICommandHandler<CancelInvitation>
{
    public string CommandType => "Identity.CancelInvitation";

    public async Task<string> HandleAsync(CancelInvitation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = InvitationSql.Reason(command.Reason);
        var row = await InvitationSql.LockAsync(context, command.InvitationId, cancellationToken).ConfigureAwait(false);
        if (row.Status is not ("REQUESTED" or "APPROVED"))
        {
            throw new DomainException(InvitationErrors.NotPending, $"The invitation is {row.Status}.");
        }

        var by = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await InvitationSql.TransitionAsync(
            context, row, "CANCELLED", CommandType, "InvitationCancelled", new { invitationId = row.Id, reason }, ", cancelled_by = @by, decided_at = @now, decision_reason = @why", cancellationToken, reason,
            ("by", by), ("now", context.Clock.UtcNow), ("why", reason)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invitationId = row.Id, status = "CANCELLED" });
    }
}

[RequiresPermission("role:revoke", StepUp = true)]
public sealed class RequestUserStatusChangeHandler : ICommandHandler<RequestUserStatusChange>
{
    public string CommandType => "Identity.RequestUserStatusChange";

    public async Task<string> HandleAsync(RequestUserStatusChange command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = InvitationSql.Reason(command.Reason);
        var action = (command.Action ?? string.Empty).Trim().ToUpperInvariant();
        if (action is not ("DISABLE" or "ENABLE"))
        {
            throw new DomainException(InvitationErrors.StatusUnchanged, "The action is DISABLE or ENABLE.");
        }

        var requester = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (requester == command.UserId)
        {
            throw new DomainException(RoleChanges.RoleChangeErrors.SelfRequest, "A user cannot disable or enable themselves.");
        }

        var status = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT status FROM iam.user WHERE user_id = @u AND kind IN ('HUMAN', 'GUEST')", r => r.GetString(0), cancellationToken, ("u", command.UserId))
            .ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(RoleChanges.RoleChangeErrors.UserInvalid, "The user does not exist or is not a person.");
        if ((action == "DISABLE") != (status == "ACTIVE"))
        {
            throw new DomainException(InvitationErrors.StatusUnchanged, $"The user is already {status}.");
        }

        await using (var open = Sql.Command(
            context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM iam.user_status_request WHERE company_id = @c AND user_id = @u AND status = 'REQUESTED')",
            ("c", context.CompanyId), ("u", command.UserId)))
        {
            if (await open.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new DomainException(InvitationErrors.StatusRequestOpen, "That user already has a pending request.");
            }
        }

        var id = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft("UserStatusChangeRequested", 1, InvitationSql.StatusAggregate, id, 1, JsonSerializer.Serialize(new { requestId = id, userId = command.UserId, action, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO iam.user_status_request (company_id, request_id, user_id, action, reason, status, requested_by, requested_at, version)
            VALUES (@c, @id, @u, @a, @why, 'REQUESTED', @by, @now, 1)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id),
            ("u", command.UserId),
            ("a", action),
            ("why", reason),
            ("by", requester),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendStateAsync(InvitationSql.StatusAggregate, id, "DOCUMENT", null, "REQUESTED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { requestId = id, status = "REQUESTED" });
    }
}

/// <summary>E-SEC-02-7: the second approver applies the change; a disabled user's open sessions stop on the next request.</summary>
[RequiresPermission("role:second_approve", StepUp = true)]
public sealed class ApproveUserStatusChangeHandler : ICommandHandler<ApproveUserStatusChange>
{
    public string CommandType => "Identity.ApproveUserStatusChange";

    public async Task<string> HandleAsync(ApproveUserStatusChange command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (userId, action, requestedBy, version) = await StatusRequests.LockOpenAsync(context, command.RequestId, cancellationToken).ConfigureAwait(false);
        var approver = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == userId || (approver == requestedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false)))
        {
            throw new DomainException(InvitationErrors.SecondApproverRequired, "The approver must be different from the requester and from the user.");
        }

        var target = action == "DISABLE" ? "DISABLED" : "ACTIVE";
        var eventId = await context.AppendEventAsync(
            new EventDraft("UserStatusChangeApproved", 1, InvitationSql.StatusAggregate, command.RequestId, version + 1, JsonSerializer.Serialize(new { requestId = command.RequestId, userId, status = target }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE iam.user_status_request SET status = 'APPROVED', second_approved_by = @by, decided_at = @now, version = version + 1 WHERE request_id = @id",
            cancellationToken,
            ("by", approver),
            ("now", context.Clock.UtcNow),
            ("id", command.RequestId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT iam.apply_user_status_request(@id)", cancellationToken, ("id", command.RequestId)).ConfigureAwait(false);
        await context.AppendStateAsync(InvitationSql.StatusAggregate, command.RequestId, "DOCUMENT", "REQUESTED", "APPROVED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { requestId = command.RequestId, status = "APPROVED", userStatus = target });
    }
}

[RequiresPermission("role:second_approve", StepUp = true)]
public sealed class RejectUserStatusChangeHandler : ICommandHandler<RejectUserStatusChange>
{
    public string CommandType => "Identity.RejectUserStatusChange";

    public async Task<string> HandleAsync(RejectUserStatusChange command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = InvitationSql.Reason(command.Reason);
        var (_, _, _, version) = await StatusRequests.LockOpenAsync(context, command.RequestId, cancellationToken).ConfigureAwait(false);
        var by = await InvitationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("UserStatusChangeRejected", 1, InvitationSql.StatusAggregate, command.RequestId, version + 1, JsonSerializer.Serialize(new { requestId = command.RequestId, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE iam.user_status_request SET status = 'REJECTED', rejected_by = @by, decided_at = @now, version = version + 1 WHERE request_id = @id",
            cancellationToken,
            ("by", by),
            ("now", context.Clock.UtcNow),
            ("id", command.RequestId)).ConfigureAwait(false);
        await context.AppendStateAsync(InvitationSql.StatusAggregate, command.RequestId, "DOCUMENT", "REQUESTED", "REJECTED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { requestId = command.RequestId, status = "REJECTED" });
    }
}

internal static class StatusRequests
{
    public static async Task<(Guid UserId, string Action, Guid RequestedBy, long Version)> LockOpenAsync(CommandContext context, Guid requestId, CancellationToken cancellationToken)
    {
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT user_id, action, requested_by, version, status FROM iam.user_status_request WHERE company_id = @c AND request_id = @id FOR UPDATE",
            r => (UserId: r.GetGuid(0), Action: r.GetString(1), RequestedBy: r.GetGuid(2), Version: r.GetInt64(3), Status: r.GetString(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", requestId)).ConfigureAwait(false)).SingleOrDefault();
        if (row == default)
        {
            throw new DomainException(InvitationErrors.StatusRequestNotFound, "The request does not exist.");
        }

        return row.Status == "REQUESTED"
            ? (row.UserId, row.Action, row.RequestedBy, row.Version)
            : throw new DomainException(InvitationErrors.NotPending, $"The request is already {row.Status}.");
    }
}
