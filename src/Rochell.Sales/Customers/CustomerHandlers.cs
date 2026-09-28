using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.MasterData.Suppliers;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Customers;

internal static class Customers
{
    public const string Aggregate = "Customer";
    public const string PartyAggregate = "Party";

    public sealed record Row(string Rnc, string LegalName, string PartyStatus, bool IsCustomer, string? CustomerStatus, long Version);

    public static async Task<Row?> LockAsync(CommandContext context, Guid partyId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT coalesce(rnc, ''), legal_name, status::text, is_customer, customer_status, version FROM md.party WHERE company_id = @c AND party_id = @p FOR UPDATE",
            r => new Row(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.NullableString(4), r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", partyId)).ConfigureAwait(false);

    public static async Task<Row> LockCustomerAsync(CommandContext context, Guid partyId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await LockAsync(context, partyId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The customer does not exist.");
        if (!row.IsCustomer)
        {
            throw new DomainException(SalesErrors.NotCustomer, "The party is not a customer.");
        }

        return expectedVersion is { } v && v != row.Version
            ? throw new DomainException(SalesErrors.VersionConflict, $"The customer changed (version {row.Version}, expected {v}); reload and retry.")
            : row;
    }

    public static (string Rnc, string LegalName) Identity(string rnc, string legalName)
    {
        var check = FormatOnlyRncRegistry.Instance.Check(rnc);
        if (!check.IsValid)
        {
            throw new DomainException(SalesErrors.RncInvalid, check.Reason ?? "Invalid RNC.");
        }

        var name = (legalName ?? string.Empty).Trim();
        return name.Length is > 0 and <= 200 ? (check.Normalized!, name) : throw new DomainException(SalesErrors.FieldRequired, "The legal name has 1 to 200 characters.");
    }

    public static (string? Phone, string? Email, string? Address) Contact(string? phone, string? email, string? address)
    {
        var e = SalesSql.Optional(email, 200, "The e-mail");
        if (e is not null && !System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            throw new DomainException(SalesErrors.FieldInvalid, "The e-mail is not valid.");
        }

        return (SalesSql.Optional(phone, 30, "The phone"), e, SalesSql.Optional(address, 300, "The address"));
    }
}

[RequiresPermission("customer:create")]
public sealed class CreateCustomerHandler : ICommandHandler<CreateCustomer>
{
    public string CommandType => "Sales.CreateCustomer";

    public async Task<string> HandleAsync(CreateCustomer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (rnc, legalName) = Customers.Identity(command.Rnc, command.LegalName);
        var (phone, email, address) = Customers.Contact(command.Phone, command.Email, command.Address);
        await SalesSql.LockAsync(context, "party-rnc:" + rnc, cancellationToken).ConfigureAwait(false);
        var existing = await SalesSql.ScalarAsync<Guid?>(context, "SELECT party_id FROM md.party WHERE company_id = @c AND rnc = @r", cancellationToken, ("c", context.CompanyId), ("r", rnc)).ConfigureAwait(false);

        if (existing is { } partyId)
        {
            // E-VS3-02-3: the supplier with this RNC becomes a customer; its identity and supplier data stay as they are.
            var row = (await Customers.LockAsync(context, partyId, cancellationToken).ConfigureAwait(false))!;
            if (row.IsCustomer)
            {
                throw new DomainException(SalesErrors.CustomerExists, $"RNC {rnc} is already a customer ({row.LegalName}).");
            }

            var version = row.Version + 1;
            var flagged = await context.AppendEventAsync(
                new EventDraft("CustomerCreated", 1, Customers.Aggregate, partyId, version, JsonSerializer.Serialize(new { partyId, rnc, legalName = row.LegalName, existingParty = true }), Publish: true),
                cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                UPDATE md.party SET is_customer = true, customer_status = 'DRAFT', phone = coalesce(@phone, phone), email = coalesce(@email, email),
                  address = coalesce(@address, address), version = @v WHERE party_id = @p
                """,
                cancellationToken,
                ("phone", phone),
                ("email", email),
                ("address", address),
                ("v", version),
                ("p", partyId)).ConfigureAwait(false);
            await context.AppendStateAsync(Customers.Aggregate, partyId, "DOCUMENT", null, "DRAFT", CommandType, flagged, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { partyId, customerStatus = "DRAFT", existingParty = true, version });
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("CustomerCreated", 1, Customers.Aggregate, context.ResultRef, 1, JsonSerializer.Serialize(new { partyId = context.ResultRef, rnc, legalName, existingParty = false }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, version, is_customer, customer_status, phone, email, address)
                VALUES (@id, @c, 'LOCAL', @rnc, @name, false, 'DRAFT', 1, true, 'DRAFT', @phone, @email, @address)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("rnc", rnc),
                ("name", legalName),
                ("phone", phone),
                ("email", email),
                ("address", address)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(SalesErrors.CustomerExists, $"A party with RNC {rnc} already exists.");
        }

        await context.AppendStateAsync(Customers.PartyAggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Customers.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyId = context.ResultRef, customerStatus = "DRAFT", existingParty = false, version = 1 });
    }
}

[RequiresPermission("customer:update")]
public sealed class UpdateCustomerHandler : ICommandHandler<UpdateCustomer>
{
    public string CommandType => "Sales.UpdateCustomer";

    public async Task<string> HandleAsync(UpdateCustomer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (rnc, legalName) = Customers.Identity(command.Rnc, command.LegalName);
        var (phone, email, address) = Customers.Contact(command.Phone, command.Email, command.Address);
        var row = await Customers.LockCustomerAsync(context, command.PartyId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.PartyStatus != "DRAFT" && (rnc != row.Rnc || legalName != row.LegalName))
        {
            throw new DomainException(SalesErrors.NotDraft, "RNC and legal name change only while the party is DRAFT (E-VS3-02-4).");
        }

        var version = row.Version + 1;
        await context.AppendEventAsync(
            new EventDraft("CustomerUpdated", 1, Customers.Aggregate, command.PartyId, version, JsonSerializer.Serialize(new { partyId = command.PartyId, rnc, legalName, phone, email, address }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE md.party SET rnc = @rnc, legal_name = @name, phone = @phone, email = @email, address = @address, version = @v WHERE party_id = @p",
                cancellationToken,
                ("rnc", rnc),
                ("name", legalName),
                ("phone", phone),
                ("email", email),
                ("address", address),
                ("v", version),
                ("p", command.PartyId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(SalesErrors.CustomerExists, $"A party with RNC {rnc} already exists.");
        }

        return JsonSerializer.Serialize(new { partyId = command.PartyId, version });
    }
}

[RequiresPermission("customer:activate", StepUp = true)]
public sealed class ActivateCustomerHandler : ICommandHandler<ActivateCustomer>
{
    public string CommandType => "Sales.ActivateCustomer";

    public async Task<string> HandleAsync(ActivateCustomer command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Customers.LockCustomerAsync(context, command.PartyId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.CustomerStatus != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The customer is {row.CustomerStatus}.");
        }

        if (await SalesSql.ScalarAsync<Guid?>(
                context, "SELECT terms_version_id FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p AND status = 'ACTIVE'", cancellationToken,
                ("c", context.CompanyId), ("p", command.PartyId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(SalesErrors.TermsRequired, "A customer is activated only with approved payment terms and credit limit (E-VS3-02-5).");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("CustomerActivated", 1, Customers.Aggregate, command.PartyId, version, JsonSerializer.Serialize(new { partyId = command.PartyId }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.party SET customer_status = 'ACTIVE', status = 'ACTIVE', version = @v WHERE party_id = @p",
            cancellationToken,
            ("v", version),
            ("p", command.PartyId)).ConfigureAwait(false);
        await context.AppendStateAsync(Customers.Aggregate, command.PartyId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        if (row.PartyStatus == "DRAFT")
        {
            await context.AppendStateAsync(Customers.PartyAggregate, command.PartyId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { partyId = command.PartyId, customerStatus = "ACTIVE", version });
    }
}

internal static class Terms
{
    public const string Aggregate = "CustomerTerms";
}

[RequiresPermission("customer_terms:prepare")]
public sealed class PrepareCustomerTermsHandler : ICommandHandler<PrepareCustomerTerms>
{
    public string CommandType => "Sales.PrepareCustomerTerms";

    public async Task<string> HandleAsync(PrepareCustomerTerms command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.PaymentTermsDays is < 0 or > 365)
        {
            throw new DomainException(SalesErrors.FieldInvalid, "Payment terms are 0 to 365 days.");
        }

        if (command.CreditLimit < 0m || decimal.Round(command.CreditLimit, 2) != command.CreditLimit)
        {
            throw new DomainException(SalesErrors.AmountInvalid, "The credit limit is zero or more, with at most 2 decimals.");
        }

        await Customers.LockCustomerAsync(context, command.PartyId, null, cancellationToken).ConfigureAwait(false);
        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var limit = command.CreditLimit.ToString("0.00", CultureInfo.InvariantCulture);
        var draft = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT terms_version_id FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p AND status = 'DRAFT' FOR UPDATE", cancellationToken,
            ("c", context.CompanyId), ("p", command.PartyId)).ConfigureAwait(false);
        var id = draft ?? context.ResultRef;
        var eventVersion = await SalesSql.NextEventVersionAsync(context, Terms.Aggregate, id, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerTermsPrepared",
                1,
                Terms.Aggregate,
                id,
                eventVersion,
                JsonSerializer.Serialize(new { termsVersionId = id, partyId = command.PartyId, paymentTermsDays = command.PaymentTermsDays, creditLimit = limit, creditHold = command.CreditHold }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            var version = (await SalesSql.ScalarAsync<int?>(
                context, "SELECT max(version) FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p", cancellationToken,
                ("c", context.CompanyId), ("p", command.PartyId)).ConfigureAwait(false) ?? 0) + 1;
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO sal.customer_terms_version (terms_version_id, company_id, party_id, version, effective_from, payment_terms_days, credit_limit, credit_hold, status, prepared_by)
                VALUES (@id, @c, @p, @v, @today, @days, @limit, @hold, 'DRAFT', @by)
                """,
                cancellationToken,
                ("id", id),
                ("c", context.CompanyId),
                ("p", command.PartyId),
                ("v", version),
                ("today", SalesSql.Today(context)),
                ("days", command.PaymentTermsDays),
                ("limit", command.CreditLimit),
                ("hold", command.CreditHold),
                ("by", preparer)).ConfigureAwait(false);
            await context.AppendStateAsync(Terms.Aggregate, id, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE sal.customer_terms_version SET payment_terms_days = @days, credit_limit = @limit, credit_hold = @hold WHERE terms_version_id = @id",
                cancellationToken,
                ("days", command.PaymentTermsDays),
                ("limit", command.CreditLimit),
                ("hold", command.CreditHold),
                ("id", id)).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { termsVersionId = id, status = "DRAFT", replaced = draft is not null });
    }
}

[RequiresPermission("customer_terms:approve", StepUp = true)]
public sealed class ApproveCustomerTermsHandler : ICommandHandler<ApproveCustomerTerms>
{
    public string CommandType => "Sales.ApproveCustomerTerms";

    private sealed record Row(Guid PartyId, int Version, string Status, Guid PreparedBy);

    public async Task<string> HandleAsync(ApproveCustomerTerms command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var partyId = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT party_id FROM sal.customer_terms_version WHERE company_id = @c AND terms_version_id = @id", cancellationToken,
            ("c", context.CompanyId), ("id", command.TermsVersionId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The terms version does not exist.");
        await Customers.LockCustomerAsync(context, partyId, null, cancellationToken).ConfigureAwait(false); // serializes a customer's approvals
        var row = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT party_id, version, status, prepared_by FROM sal.customer_terms_version WHERE terms_version_id = @id FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3)),
            cancellationToken,
            ("id", command.TermsVersionId)).ConfigureAwait(false))!;
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The terms version is {row.Status}.");
        }

        var approver = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy)
        {
            throw new DomainException(SalesErrors.FourEyes, "Customer terms are approved by someone other than who prepared them.");
        }

        var previous = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT terms_version_id FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p AND status = 'ACTIVE' FOR UPDATE", cancellationToken,
            ("c", context.CompanyId), ("p", row.PartyId)).ConfigureAwait(false);
        var today = SalesSql.Today(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerTermsApproved",
                1,
                Terms.Aggregate,
                command.TermsVersionId,
                await SalesSql.NextEventVersionAsync(context, Terms.Aggregate, command.TermsVersionId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { termsVersionId = command.TermsVersionId, partyId = row.PartyId, version = row.Version, effectiveFrom = today, supersedes = previous }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (previous is { } old)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE sal.customer_terms_version SET status = 'SUPERSEDED' WHERE terms_version_id = @id", cancellationToken, ("id", old)).ConfigureAwait(false);
            await context.AppendStateAsync(Terms.Aggregate, old, "DOCUMENT", "ACTIVE", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE sal.customer_terms_version SET status = 'ACTIVE', approved_by = @by, effective_from = @today WHERE terms_version_id = @id",
            cancellationToken,
            ("by", approver),
            ("today", today),
            ("id", command.TermsVersionId)).ConfigureAwait(false);
        await context.AppendStateAsync(Terms.Aggregate, command.TermsVersionId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { termsVersionId = command.TermsVersionId, status = "ACTIVE", effectiveFrom = today, superseded = previous });
    }
}
