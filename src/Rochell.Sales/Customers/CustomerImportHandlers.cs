using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.MasterData;
using Rochell.MasterData.Import;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Customers;

/// <summary>E-IMP-1: what importing the customer file would do; nothing is written.</summary>
public sealed record PreviewCustomerImport(Guid CompanyId, Guid SessionId, string FileName, string ContentBase64) : IQuery;

[RequiresPermission("customer:import")]
public sealed class PreviewCustomerImportHandler : IQueryHandler<PreviewCustomerImport>
{
    public string QueryType => "Sales.PreviewCustomerImport";

    public async Task<string> HandleAsync(PreviewCustomerImport query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var file = PartyImport.Parse(query.FileName, query.ContentBase64, customers: true);
        return ApiJson.Serialize(await PartyImport.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, file, customers: true, cancellationToken).ConfigureAwait(false));
    }
}

[RequiresPermission("customer:import", StepUp = true)]
public sealed class ImportCustomersHandler : ICommandHandler<ImportCustomers>
{
    public const string Aggregate = "PartyImport";

    public string CommandType => "Sales.ImportCustomers";

    public async Task<string> HandleAsync(ImportCustomers command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var file = PartyImport.Parse(command.FileName, command.ContentBase64, customers: true);

        // One import at a time per company (the same lock as the supplier import): analysis and inserts see the same parties.
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('party-import:' || @c, 0))", cancellationToken, ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var preview = await PartyImport.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, file, customers: true, cancellationToken).ConfigureAwait(false);
        var preparer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var today = SalesSql.Today(context);

        var items = new List<PartyImportRow>(preview.Items.Count);
        foreach (var row in preview.Items)
        {
            if (row.Outcome is not (PartyImport.Create or PartyImport.Link))
            {
                items.Add(row);
                continue;
            }

            var partyId = row.Outcome == PartyImport.Link
                ? await LinkAsync(row, context, cancellationToken).ConfigureAwait(false)
                : await CreateAsync(row, context, cancellationToken).ConfigureAwait(false);
            await PrepareTermsAsync(row, partyId, preparer, today, context, cancellationToken).ConfigureAwait(false);
            items.Add(row with { PartyId = partyId });
        }

        await context.AppendEventAsync(
            new EventDraft(
                "CustomersImported",
                1,
                Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    importId = context.ResultRef,
                    fileName = preview.FileName,
                    sha256 = preview.Sha256,
                    rows = preview.Rows,
                    created = preview.ToCreate,
                    linked = preview.ToLink,
                    existing = preview.Existing,
                    duplicates = preview.Duplicates,
                    rejected = preview.Rejected,
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(preview with { Items = items });
    }

    private async Task<Guid> CreateAsync(PartyImportRow row, CommandContext context, CancellationToken cancellationToken)
    {
        var partyId = context.Ids.NewId();
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerCreated",
                1,
                Customers.Aggregate,
                partyId,
                1,
                JsonSerializer.Serialize(new { partyId, rnc = row.Rnc, legalName = row.LegalName, existingParty = false, importId = context.ResultRef }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, version, is_customer, customer_status, phone, email)
                VALUES (@id, @c, 'LOCAL', @rnc, @name, false, 'DRAFT', 1, true, 'DRAFT', @phone, @email)
                """,
                cancellationToken,
                ("id", partyId),
                ("c", context.CompanyId),
                ("rnc", row.Rnc),
                ("name", row.LegalName),
                ("phone", row.Phone),
                ("email", row.Emails.Count > 0 ? row.Emails[0] : null)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(SalesErrors.CustomerExists, $"RNC {row.Rnc} was created by someone else while the file was loading; nothing was imported, load it again.");
        }

        await PartyEmails.ReplaceAsync(context.Connection, context.Transaction, context.CompanyId, partyId, row.Emails, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Customers.PartyAggregate, partyId, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Customers.Aggregate, partyId, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return partyId;
    }

    /// <summary>E-VS3-02-3, E-IMP-5: the supplier with this RNC becomes a customer; its identity and supplier data stay.</summary>
    private async Task<Guid> LinkAsync(PartyImportRow row, CommandContext context, CancellationToken cancellationToken)
    {
        var partyId = row.PartyId!.Value;
        var party = (await Customers.LockAsync(context, partyId, cancellationToken).ConfigureAwait(false))!;
        var apply = row.Emails.Count > 0 && (await PartyEmails.ListAsync(context.Connection, context.Transaction, context.CompanyId, partyId, cancellationToken).ConfigureAwait(false)).Count == 0;
        var version = party.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerCreated",
                1,
                Customers.Aggregate,
                partyId,
                version,
                JsonSerializer.Serialize(new { partyId, rnc = row.Rnc, legalName = party.LegalName, existingParty = true, importId = context.ResultRef }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.party SET is_customer = true, customer_status = 'DRAFT', phone = coalesce(phone, @phone), email = coalesce(@email, email), version = @v WHERE party_id = @p",
            cancellationToken,
            ("phone", row.Phone),
            ("email", apply ? row.Emails[0] : null),
            ("v", version),
            ("p", partyId)).ConfigureAwait(false);
        if (apply)
        {
            await PartyEmails.ReplaceAsync(context.Connection, context.Transaction, context.CompanyId, partyId, row.Emails, cancellationToken).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Customers.Aggregate, partyId, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return partyId;
    }

    /// <summary>E-IMP-8, E-IMP-01-7: the customer's first terms, DRAFT, for Crédito to complete and the Controller to approve.</summary>
    private async Task PrepareTermsAsync(PartyImportRow row, Guid partyId, Guid preparer, DateOnly today, CommandContext context, CancellationToken cancellationToken)
    {
        var termsId = context.Ids.NewId();
        var days = row.PaymentTermsDays ?? 0;
        var limit = decimal.Parse(row.CreditLimit!, CultureInfo.InvariantCulture);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomerTermsPrepared",
                1,
                Terms.Aggregate,
                termsId,
                1,
                JsonSerializer.Serialize(new { termsVersionId = termsId, partyId, paymentTermsDays = days, creditLimit = row.CreditLimit, creditHold = false }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.customer_terms_version (terms_version_id, company_id, party_id, version, effective_from, payment_terms_days, credit_limit, credit_hold, status, prepared_by)
            VALUES (@id, @c, @p, 1, @today, @days, @limit, false, 'DRAFT', @by)
            """,
            cancellationToken,
            ("id", termsId),
            ("c", context.CompanyId),
            ("p", partyId),
            ("today", today),
            ("days", days),
            ("limit", limit),
            ("by", preparer)).ConfigureAwait(false);
        await context.AppendStateAsync(Terms.Aggregate, termsId, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
    }
}

internal static class Batches
{
    public const int Max = 500;

    public static List<Guid> Ids(IReadOnlyList<Guid>? ids, string what)
    {
        var list = (ids ?? []).Distinct().Order().ToList();
        return list.Count is 0 or > Max ? throw new DomainException(MasterDataErrors.BatchInvalid, $"Between 1 and {Max} {what} are processed at once.") : list;
    }
}

[RequiresPermission("customer_terms:approve", StepUp = true)]
public sealed class ApproveCustomerTermsBatchHandler : ICommandHandler<ApproveCustomerTermsBatch>
{
    public string CommandType => "Sales.ApproveCustomerTermsBatch";

    public async Task<string> HandleAsync(ApproveCustomerTermsBatch command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = Batches.Ids(command.TermsVersionIds, "terms versions");
        var items = new List<object>(ids.Count);
        var approved = 0;
        foreach (var id in ids)
        {
            try
            {
                var (effectiveFrom, superseded) = await Terms.ApproveAsync(context, CommandType, id, cancellationToken).ConfigureAwait(false);
                items.Add(new { termsVersionId = id, outcome = "DONE", effectiveFrom, superseded });
                approved++;
            }
            catch (DomainException ex)
            {
                // Terms.ApproveAsync checks before it writes: a refused version left nothing behind.
                items.Add(new { termsVersionId = id, outcome = "SKIPPED", code = ex.Code, message = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new { requested = ids.Count, approved, skipped = ids.Count - approved, items });
    }
}

[RequiresPermission("customer:activate", StepUp = true)]
public sealed class ActivateCustomersHandler : ICommandHandler<ActivateCustomers>
{
    public string CommandType => "Sales.ActivateCustomers";

    public async Task<string> HandleAsync(ActivateCustomers command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = Batches.Ids(command.PartyIds, "customers");
        var items = new List<object>(ids.Count);
        var activated = 0;
        foreach (var id in ids)
        {
            try
            {
                var version = await Customers.ActivateAsync(context, CommandType, id, null, cancellationToken).ConfigureAwait(false);
                items.Add(new { partyId = id, outcome = "DONE", version });
                activated++;
            }
            catch (DomainException ex)
            {
                // Customers.ActivateAsync checks before it writes: a refused customer left nothing behind.
                items.Add(new { partyId = id, outcome = "SKIPPED", code = ex.Code, message = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new { requested = ids.Count, activated, skipped = ids.Count - activated, items });
    }
}
