using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Tax.Authorizations;

internal static class AuthorizationStore
{
    public const string Aggregate = "FiscalAuthorization";

    public sealed record Row(Guid Id, Guid PartyId, string Status, Guid RegisteredBy, DateOnly? ValidUntil, long Version);

    public sealed record Header(string CertificateNo, DateOnly IssuedOn, DateOnly? ValidUntil, string ProjectName, string ResolutionNo, DateOnly? ProjectTermEndsOn, Guid? SalesOrderId);

    public sealed record Line(Guid ItemId, string Uom, decimal Quantity, decimal Net);

    public static DateOnly Today(CommandContext context) => BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);

    public static async Task<T?> ScalarAsync<T>(CommandContext context, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Events are numbered after the ones already recorded (documents add events without changing the row's version).</summary>
    public static async Task<long> NextEventVersionAsync(CommandContext context, Guid id, CancellationToken cancellationToken)
        => (await ScalarAsync<long?>(
               context, "SELECT max(aggregate_version) FROM core.domain_event WHERE company_id = @c AND aggregate_type = @t AND aggregate_id = @a", cancellationToken,
               ("c", context.CompanyId), ("t", Aggregate), ("a", id)).ConfigureAwait(false) ?? 0) + 1;

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
        => (await ScalarAsync<Guid?>(context, "SELECT user_id FROM iam.session WHERE session_id = @s", cancellationToken, ("s", context.SessionId)).ConfigureAwait(false))!.Value;

    /// <summary>Locks the authorization (FOR UPDATE) and checks the expected version.</summary>
    public static async Task<Row> LockAsync(CommandContext context, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT authorization_id, party_id, status, registered_by, valid_until, version FROM tax.fiscal_authorization WHERE company_id = @c AND authorization_id = @id FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.IsDBNull(4) ? null : r.Date(4), r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id)).ConfigureAwait(false)
            ?? throw new DomainException(TaxErrors.AuthorizationNotFound, "The fiscal authorization does not exist.");
        return expectedVersion is null || row.Version == expectedVersion
            ? row
            : throw new DomainException(TaxErrors.AuthorizationVersionConflict, $"The authorization changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    public static void RequireStatus(Row row, params string[] statuses)
    {
        if (!statuses.Contains(row.Status))
        {
            throw new DomainException(TaxErrors.AuthorizationInvalidState, $"The authorization is {row.Status}.");
        }
    }

    public static string Reason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= 500 ? trimmed : throw new DomainException(TaxErrors.AuthorizationReasonRequired, "A reason of 1 to 500 characters is required.");
    }

    /// <summary>Changes the status (+1 version) with its event and state history.</summary>
    public static async Task<string> TransitionAsync(
        CommandContext context, Row row, string to, string eventType, string commandType, CancellationToken cancellationToken, string? reason = null, Guid? verifiedBy = null)
    {
        var next = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.Id, await NextEventVersionAsync(context, row.Id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { authorizationId = row.Id, status = to, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE tax.fiscal_authorization SET status = @s, verified_by = coalesce(CAST(@v AS uuid), verified_by), version = @n WHERE authorization_id = @id",
            cancellationToken,
            ("s", to),
            ("v", verifiedBy),
            ("n", next),
            ("id", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { authorizationId = row.Id, status = to, version = next });
    }

    public static Header ValidateHeader(string? certificateNo, DateOnly issuedOn, DateOnly? validUntil, string? projectName, string? resolutionNo, DateOnly? projectTermEndsOn, Guid? salesOrderId)
    {
        static string Text(string? value, int max, string what)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length > 0 && trimmed.Length <= max ? trimmed : throw new DomainException(TaxErrors.AuthorizationFieldInvalid, $"{what} has 1 to {max} characters.");
        }

        if (validUntil is { } until && until < issuedOn)
        {
            throw new DomainException(TaxErrors.AuthorizationFieldInvalid, "The certificate is valid until a date on or after its issue.");
        }

        return new Header(Text(certificateNo, 60, "The certificate number"), issuedOn, validUntil, Text(projectName, 200, "The project name"), Text(resolutionNo, 60, "The CONFOTUR resolution number"), projectTermEndsOn, salesOrderId);
    }

    /// <summary>E-FIS1-02-2: the customer, the source order and each scope line are checked against the masters.</summary>
    public static async Task<List<Line>> ValidateAsync(CommandContext context, Guid partyId, Header header, IReadOnlyList<AuthorizationLineInput>? lines, CancellationToken cancellationToken)
    {
        if (await ScalarAsync<string>(
                context, "SELECT customer_status FROM md.party WHERE company_id = @c AND party_id = @p AND is_customer AND rnc IS NOT NULL", cancellationToken,
                ("c", context.CompanyId), ("p", partyId)).ConfigureAwait(false) != "ACTIVE")
        {
            throw new DomainException(TaxErrors.AuthorizationCustomerInvalid, "An authorization is of an ACTIVE customer with an RNC.");
        }

        if (header.SalesOrderId is { } order && await ScalarAsync<Guid?>(
                context, "SELECT sales_order_id FROM sal.sales_order WHERE company_id = @c AND sales_order_id = @o AND party_id = @p", cancellationToken,
                ("c", context.CompanyId), ("o", order), ("p", partyId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(TaxErrors.AuthorizationFieldInvalid, "The source order is not an order of the customer.");
        }

        var input = lines ?? [];
        if (input.Count == 0)
        {
            throw new DomainException(TaxErrors.AuthorizationLinesRequired, "An authorization covers at least one product.");
        }

        var today = Today(context);
        var result = new List<Line>();
        foreach (var line in input)
        {
            var uom = (line.Uom ?? string.Empty).Trim();
            if (result.Any(r => r.ItemId == line.ItemId && r.Uom == uom))
            {
                throw new DomainException(TaxErrors.AuthorizationFieldInvalid, "Each product and unit appears once in the scope.");
            }

            var baseUom = await ScalarAsync<string>(
                context, "SELECT base_uom FROM md.item WHERE company_id = @c AND item_id = @i AND item_type = 'FINISHED_GOOD' AND status::text = 'ACTIVE'", cancellationToken,
                ("c", context.CompanyId), ("i", line.ItemId)).ConfigureAwait(false)
                ?? throw new DomainException(TaxErrors.AuthorizationItemInvalid, $"Item {line.ItemId} is not an ACTIVE finished good.");
            if (uom != baseUom && await ScalarAsync<string>(
                    context,
                    """
                    SELECT from_uom FROM md.uom_conversion
                    WHERE company_id = @c AND item_id = @i AND from_uom = @u AND to_uom = @b AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
                    """,
                    cancellationToken,
                    ("c", context.CompanyId),
                    ("i", line.ItemId),
                    ("u", uom),
                    ("b", baseUom),
                    ("d", today)).ConfigureAwait(false) is null)
            {
                throw new DomainException(TaxErrors.AuthorizationItemInvalid, $"Unit {uom} is neither the base unit ({baseUom}) nor has a conversion in force.");
            }

            if (line.Quantity <= 0m || decimal.Round(line.Quantity, 6) != line.Quantity || line.NetAmount <= 0m || decimal.Round(line.NetAmount, 2) != line.NetAmount)
            {
                throw new DomainException(TaxErrors.AuthorizationFieldInvalid, "Each scope line has a quantity above zero (6 decimals) and a net amount above zero (2 decimals).");
            }

            result.Add(new Line(line.ItemId, uom, line.Quantity, line.NetAmount));
        }

        return result;
    }

    public static async Task InsertLinesAsync(CommandContext context, Guid id, List<Line> lines, CancellationToken cancellationToken)
    {
        var no = 0;
        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO tax.fiscal_authorization_line (authorization_id, line_no, company_id, item_id, uom, qty_authorized, net_authorized, qty_consumed, net_consumed)
                VALUES (@id, @n, @c, @i, @u, @q, @a, 0, 0)
                """,
                cancellationToken,
                ("id", id),
                ("n", ++no),
                ("c", context.CompanyId),
                ("i", line.ItemId),
                ("u", line.Uom),
                ("q", line.Quantity),
                ("a", line.Net)).ConfigureAwait(false);
        }
    }

    public static object Payload(Guid id, Guid partyId, Header header, List<Line> lines) => new
    {
        authorizationId = id,
        partyId,
        certificateNo = header.CertificateNo,
        issuedOn = header.IssuedOn,
        validUntil = header.ValidUntil,
        projectName = header.ProjectName,
        confoturResolutionNo = header.ResolutionNo,
        projectTermEndsOn = header.ProjectTermEndsOn,
        salesOrderId = header.SalesOrderId,
        lines = lines.Select(l => new { itemId = l.ItemId, uom = l.Uom, quantity = l.Quantity.ToString(CultureInfo.InvariantCulture), netAmount = l.Net.ToString("0.00", CultureInfo.InvariantCulture) }),
    };
}

[RequiresPermission("fiscal_authorization:register")]
public sealed class RegisterFiscalAuthorizationHandler : ICommandHandler<RegisterFiscalAuthorization>
{
    public string CommandType => "Tax.RegisterFiscalAuthorization";

    public async Task<string> HandleAsync(RegisterFiscalAuthorization command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = AuthorizationStore.ValidateHeader(command.CertificateNo, command.IssuedOn, command.ValidUntil, command.ProjectName, command.ConfoturResolutionNo, command.ProjectTermEndsOn, command.SalesOrderId);
        var lines = await AuthorizationStore.ValidateAsync(context, command.PartyId, header, command.Lines, cancellationToken).ConfigureAwait(false);
        var registrar = await AuthorizationStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var id = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft("FiscalAuthorizationRegistered", 1, AuthorizationStore.Aggregate, id, 1, JsonSerializer.Serialize(AuthorizationStore.Payload(id, command.PartyId, header, lines)), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO tax.fiscal_authorization (authorization_id, company_id, party_id, regime, certificate_no, issued_on, valid_until, project_name, confotur_resolution_no,
                                                      project_term_ends_on, sales_order_id, status, registered_by, version)
                VALUES (@id, @c, @p, 'CONFOTUR', @cert, @issued, @until, @project, @resolution, @term, @order, 'DRAFT', @by, 1)
                """,
                cancellationToken,
                ("id", id),
                ("c", context.CompanyId),
                ("p", command.PartyId),
                ("cert", header.CertificateNo),
                ("issued", header.IssuedOn),
                ("until", header.ValidUntil),
                ("project", header.ProjectName),
                ("resolution", header.ResolutionNo),
                ("term", header.ProjectTermEndsOn),
                ("order", header.SalesOrderId),
                ("by", registrar)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(TaxErrors.AuthorizationCertificateDuplicate, $"Certificate {header.CertificateNo} is already registered.");
        }

        await AuthorizationStore.InsertLinesAsync(context, id, lines, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(AuthorizationStore.Aggregate, id, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { authorizationId = id, status = "DRAFT", version = 1, lines = lines.Count });
    }
}

[RequiresPermission("fiscal_authorization:register")]
public sealed class UpdateDraftAuthorizationHandler : ICommandHandler<UpdateDraftAuthorization>
{
    public string CommandType => "Tax.UpdateDraftAuthorization";

    public async Task<string> HandleAsync(UpdateDraftAuthorization command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = AuthorizationStore.ValidateHeader(command.CertificateNo, command.IssuedOn, command.ValidUntil, command.ProjectName, command.ConfoturResolutionNo, command.ProjectTermEndsOn, command.SalesOrderId);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "DRAFT");
        var lines = await AuthorizationStore.ValidateAsync(context, row.PartyId, header, command.Lines, cancellationToken).ConfigureAwait(false);
        var next = row.Version + 1;
        await context.AppendEventAsync(
            new EventDraft("FiscalAuthorizationUpdated", 1, AuthorizationStore.Aggregate, row.Id, await AuthorizationStore.NextEventVersionAsync(context, row.Id, cancellationToken).ConfigureAwait(false), JsonSerializer.Serialize(AuthorizationStore.Payload(row.Id, row.PartyId, header, lines)), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                UPDATE tax.fiscal_authorization SET certificate_no = @cert, issued_on = @issued, valid_until = @until, project_name = @project, confotur_resolution_no = @resolution,
                       project_term_ends_on = @term, sales_order_id = @order, version = @n
                WHERE authorization_id = @id
                """,
                cancellationToken,
                ("cert", header.CertificateNo),
                ("issued", header.IssuedOn),
                ("until", header.ValidUntil),
                ("project", header.ProjectName),
                ("resolution", header.ResolutionNo),
                ("term", header.ProjectTermEndsOn),
                ("order", header.SalesOrderId),
                ("n", next),
                ("id", row.Id)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(TaxErrors.AuthorizationCertificateDuplicate, $"Certificate {header.CertificateNo} is already registered.");
        }

        await Sql.ExecuteAsync(context.Connection, context.Transaction, "DELETE FROM tax.fiscal_authorization_line WHERE authorization_id = @id", cancellationToken, ("id", row.Id)).ConfigureAwait(false);
        await AuthorizationStore.InsertLinesAsync(context, row.Id, lines, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { authorizationId = row.Id, status = "DRAFT", version = next, lines = lines.Count });
    }
}

[RequiresPermission("fiscal_authorization:register")]
public sealed class AttachAuthorizationDocumentHandler : ICommandHandler<AttachAuthorizationDocument>
{
    private static readonly string[] Kinds = ["CERTIFICADO_DGII", "RESOLUCION_CONFOTUR", "LISTA_MATERIALES", "PROFORMA"];

    public string CommandType => "Tax.AttachAuthorizationDocument";

    public async Task<string> HandleAsync(AttachAuthorizationDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var kind = (command.Kind ?? string.Empty).Trim();
        var reference = (command.EvidenceRef ?? string.Empty).Trim();
        var sha = (command.EvidenceSha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind) || reference.Length is 0 or > 300 || sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
        {
            throw new DomainException(TaxErrors.AuthorizationFieldInvalid, "A document has a kind (CERTIFICADO_DGII, RESOLUCION_CONFOTUR, LISTA_MATERIALES, PROFORMA), a reference and its SHA-256.");
        }

        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, null, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "DRAFT", "PENDING_VERIFICATION", "ACTIVE", "SUSPENDED", "EXHAUSTED");
        var user = await AuthorizationStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft(
                "FiscalAuthorizationDocumentAttached",
                1,
                AuthorizationStore.Aggregate,
                row.Id,
                await AuthorizationStore.NextEventVersionAsync(context, row.Id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { authorizationId = row.Id, documentId = context.ResultRef, kind, evidenceRef = reference, evidenceSha256 = sha }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.fiscal_authorization_document (document_id, company_id, authorization_id, kind, evidence_ref, evidence_sha256, added_by, added_at)
            VALUES (@id, @c, @a, @k, @r, @s, @by, @at)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("a", row.Id),
            ("k", kind),
            ("r", reference),
            ("s", sha),
            ("by", user),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { documentId = context.ResultRef, authorizationId = row.Id, kind });
    }
}

[RequiresPermission("fiscal_authorization:register")]
public sealed class SubmitForVerificationHandler : ICommandHandler<SubmitForVerification>
{
    public string CommandType => "Tax.SubmitForVerification";

    public async Task<string> HandleAsync(SubmitForVerification command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "DRAFT");
        if (await AuthorizationStore.ScalarAsync<long?>(context, "SELECT count(*) FROM tax.fiscal_authorization_line WHERE authorization_id = @id", cancellationToken, ("id", row.Id)).ConfigureAwait(false) is null or 0)
        {
            throw new DomainException(TaxErrors.AuthorizationLinesRequired, "An authorization covers at least one product.");
        }

        if (await AuthorizationStore.ScalarAsync<long?>(
                context, "SELECT count(*) FROM tax.fiscal_authorization_document WHERE authorization_id = @id AND kind = 'CERTIFICADO_DGII'", cancellationToken, ("id", row.Id)).ConfigureAwait(false) is null or 0)
        {
            throw new DomainException(TaxErrors.AuthorizationCertificateRequired, "Attach the DGII exemption certificate before submitting (E-FIS1-02-4).");
        }

        return await AuthorizationStore.TransitionAsync(context, row, "PENDING_VERIFICATION", "FiscalAuthorizationSubmitted", CommandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("fiscal_authorization:verify", StepUp = true)]
public sealed class VerifyAuthorizationHandler : ICommandHandler<VerifyAuthorization>
{
    public string CommandType => "Tax.VerifyAuthorization";

    public async Task<string> HandleAsync(VerifyAuthorization command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "PENDING_VERIFICATION");
        var verifier = await AuthorizationStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (verifier == row.RegisteredBy)
        {
            throw new DomainException(TaxErrors.AuthorizationSamePerson, "An authorization is verified by someone other than who registered it (E-FIS1-3).");
        }

        if (row.ValidUntil is { } until && until < AuthorizationStore.Today(context))
        {
            throw new DomainException(TaxErrors.AuthorizationExpired, $"The certificate was valid until {until:yyyy-MM-dd} (E-FIS1-02-9).");
        }

        return await AuthorizationStore.TransitionAsync(context, row, "ACTIVE", "FiscalAuthorizationVerified", CommandType, cancellationToken, verifiedBy: verifier).ConfigureAwait(false);
    }
}

[RequiresPermission("fiscal_authorization:verify")]
public sealed class ReturnAuthorizationToDraftHandler : ICommandHandler<ReturnAuthorizationToDraft>
{
    public string CommandType => "Tax.ReturnAuthorizationToDraft";

    public async Task<string> HandleAsync(ReturnAuthorizationToDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = AuthorizationStore.Reason(command.Reason);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "PENDING_VERIFICATION");
        return await AuthorizationStore.TransitionAsync(context, row, "DRAFT", "FiscalAuthorizationReturned", CommandType, cancellationToken, reason).ConfigureAwait(false);
    }
}

[RequiresPermission("fiscal_authorization:verify")]
public sealed class RejectAuthorizationHandler : ICommandHandler<RejectAuthorization>
{
    public string CommandType => "Tax.RejectAuthorization";

    public async Task<string> HandleAsync(RejectAuthorization command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = AuthorizationStore.Reason(command.Reason);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "PENDING_VERIFICATION");
        return await AuthorizationStore.TransitionAsync(context, row, "REJECTED", "FiscalAuthorizationRejected", CommandType, cancellationToken, reason).ConfigureAwait(false);
    }
}

[RequiresPermission("fiscal_authorization:suspend", StepUp = true)]
public sealed class SuspendAuthorizationHandler : ICommandHandler<SuspendAuthorization>
{
    public string CommandType => "Tax.SuspendAuthorization";

    public async Task<string> HandleAsync(SuspendAuthorization command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = AuthorizationStore.Reason(command.Reason);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "ACTIVE");
        return await AuthorizationStore.TransitionAsync(context, row, "SUSPENDED", "FiscalAuthorizationSuspended", CommandType, cancellationToken, reason).ConfigureAwait(false);
    }
}

[RequiresPermission("fiscal_authorization:suspend", StepUp = true)]
public sealed class ReactivateAuthorizationHandler : ICommandHandler<ReactivateAuthorization>
{
    public string CommandType => "Tax.ReactivateAuthorization";

    public async Task<string> HandleAsync(ReactivateAuthorization command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = AuthorizationStore.Reason(command.Reason);
        var row = await AuthorizationStore.LockAsync(context, command.AuthorizationId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        AuthorizationStore.RequireStatus(row, "SUSPENDED");
        if (row.ValidUntil is { } until && until < AuthorizationStore.Today(context))
        {
            throw new DomainException(TaxErrors.AuthorizationExpired, $"The certificate was valid until {until:yyyy-MM-dd}.");
        }

        return await AuthorizationStore.TransitionAsync(context, row, "ACTIVE", "FiscalAuthorizationReactivated", CommandType, cancellationToken, reason).ConfigureAwait(false);
    }
}

/// <summary>
/// E-FIS1-04-5: expiry is a date rule without judgement, so it takes no step-up (the daily run has nobody to confirm it). Rows are
/// locked in id order so a manual run and the daily run never deadlock; the second one finds nothing left to expire.
/// </summary>
[RequiresPermission("fiscal_authorization:suspend")]
public sealed class ExpireFiscalAuthorizationsHandler : ICommandHandler<ExpireFiscalAuthorizations>
{
    public string CommandType => "Tax.ExpireFiscalAuthorizations";

    public async Task<string> HandleAsync(ExpireFiscalAuthorizations command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT authorization_id FROM tax.fiscal_authorization
            WHERE company_id = @c AND status IN ('ACTIVE', 'SUSPENDED', 'EXHAUSTED') AND valid_until < @today ORDER BY authorization_id
            """,
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("today", AuthorizationStore.Today(context))).ConfigureAwait(false);
        var expired = new List<Guid>();
        foreach (var id in ids)
        {
            var row = await AuthorizationStore.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
            if (row.Status is "ACTIVE" or "SUSPENDED" or "EXHAUSTED" && row.ValidUntil < AuthorizationStore.Today(context))
            {
                await AuthorizationStore.TransitionAsync(context, row, "EXPIRED", "FiscalAuthorizationExpired", CommandType, cancellationToken).ConfigureAwait(false);
                expired.Add(id);
            }
        }

        return JsonSerializer.Serialize(new { expired });
    }
}
