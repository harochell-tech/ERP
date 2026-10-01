using System.Text.Json;
using Rochell.MasterData.Import;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.MasterData.Suppliers;

/// <summary>E-IMP-1: what importing the supplier file would do; nothing is written.</summary>
public sealed record PreviewSupplierImport(Guid CompanyId, Guid SessionId, string FileName, string ContentBase64) : IQuery;

[RequiresPermission("supplier:import")]
public sealed class PreviewSupplierImportHandler : IQueryHandler<PreviewSupplierImport>
{
    public string QueryType => "MasterData.PreviewSupplierImport";

    public async Task<string> HandleAsync(PreviewSupplierImport query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var file = PartyImport.Parse(query.FileName, query.ContentBase64, customers: false);
        return ApiJson.Serialize(await PartyImport.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, file, customers: false, cancellationToken).ConfigureAwait(false));
    }
}

[RequiresPermission("supplier:import", StepUp = true)]
public sealed class ImportSuppliersHandler : ICommandHandler<ImportSuppliers>
{
    public const string Aggregate = "PartyImport";

    public string CommandType => "MasterData.ImportSuppliers";

    public async Task<string> HandleAsync(ImportSuppliers command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var file = PartyImport.Parse(command.FileName, command.ContentBase64, customers: false);

        // One import at a time per company: the analysis and the inserts see the same parties.
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('party-import:' || @c, 0))", cancellationToken, ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var preview = await PartyImport.AnalyzeAsync(context.Connection, context.Transaction, context.CompanyId, file, customers: false, cancellationToken).ConfigureAwait(false);

        var items = new List<PartyImportRow>(preview.Items.Count);
        foreach (var row in preview.Items)
        {
            if (row.Outcome != PartyImport.Create)
            {
                items.Add(row);
                continue;
            }

            var partyId = context.Ids.NewId();
            var eventId = await context.AppendEventAsync(
                new EventDraft(
                    "SupplierCreated",
                    1,
                    SupplierRules.Aggregate,
                    partyId,
                    1,
                    JsonSerializer.Serialize(new { partyId, rnc = row.Rnc, legalName = row.LegalName, partyKind = "LOCAL", importId = context.ResultRef }),
                    Publish: false),
                cancellationToken).ConfigureAwait(false);
            try
            {
                await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    """
                    INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, version, phone, email, supplier_payment_terms_days)
                    VALUES (@id, @c, 'LOCAL', @rnc, @name, true, 'DRAFT', 1, @phone, @email, @days)
                    """,
                    cancellationToken,
                    ("id", partyId),
                    ("c", context.CompanyId),
                    ("rnc", row.Rnc),
                    ("name", row.LegalName),
                    ("phone", row.Phone),
                    ("email", row.Emails.Count > 0 ? row.Emails[0] : null),
                    ("days", row.PaymentTermsDays)).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException ex) when (MasterRows.IsUniqueViolation(ex))
            {
                throw new DomainException(MasterDataErrors.RncDuplicate, $"RNC {row.Rnc} was created by someone else while the file was loading; nothing was imported, load it again.");
            }

            await PartyEmails.ReplaceAsync(context.Connection, context.Transaction, context.CompanyId, partyId, row.Emails, cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(SupplierRules.Aggregate, partyId, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
            items.Add(row with { PartyId = partyId });
        }

        await context.AppendEventAsync(
            new EventDraft(
                "SuppliersImported",
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
                    existing = preview.Existing,
                    duplicates = preview.Duplicates,
                    rejected = preview.Rejected,
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(preview with { Items = items });
    }
}

[RequiresPermission("supplier:activate", StepUp = true)]
public sealed class ActivateSuppliersHandler : ICommandHandler<ActivateSuppliers>
{
    public const int MaxBatch = 500;

    public string CommandType => "MasterData.ActivateSuppliers";

    private sealed record Row(bool IsSupplier, string Status, long Version);

    public async Task<string> HandleAsync(ActivateSuppliers command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = (command.PartyIds ?? []).Distinct().Order().ToList();
        if (ids.Count is 0 or > MaxBatch)
        {
            throw new DomainException(MasterDataErrors.BatchInvalid, $"Between 1 and {MaxBatch} suppliers are activated at once.");
        }

        var items = new List<object>(ids.Count);
        var activated = 0;
        foreach (var partyId in ids)
        {
            var row = await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                "SELECT is_supplier, status::text, version FROM md.party WHERE company_id = @c AND party_id = @p FOR UPDATE",
                r => new Row(r.GetBoolean(0), r.GetString(1), r.GetInt64(2)),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", partyId)).ConfigureAwait(false);
            if (row is not { IsSupplier: true })
            {
                items.Add(new { partyId, outcome = "SKIPPED", code = MasterDataErrors.NotFound });
                continue;
            }

            if (row.Status != "DRAFT")
            {
                items.Add(new { partyId, outcome = "SKIPPED", code = MasterDataErrors.NotDraft });
                continue;
            }

            var version = row.Version + 1;
            var eventId = await context.AppendEventAsync(
                new EventDraft("SupplierActivated", 1, SupplierRules.Aggregate, partyId, version, JsonSerializer.Serialize(new { partyId }), Publish: true),
                cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE md.party SET status = 'ACTIVE', version = @v WHERE party_id = @p", cancellationToken, ("v", version), ("p", partyId)).ConfigureAwait(false);
            await context.AppendStateAsync(SupplierRules.Aggregate, partyId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
            items.Add(new { partyId, outcome = "DONE", version });
            activated++;
        }

        return JsonSerializer.Serialize(new { requested = ids.Count, activated, skipped = ids.Count - activated, items });
    }
}

[RequiresPermission("supplier:update")]
public sealed class SetSupplierContactHandler : ICommandHandler<SetSupplierContact>
{
    public string CommandType => "MasterData.SetSupplierContact";

    private sealed record Row(bool IsSupplier, long Version);

    public async Task<string> HandleAsync(SetSupplierContact command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var phone = string.IsNullOrWhiteSpace(command.Phone) ? null : command.Phone.Trim();
        if (phone is { Length: > 30 })
        {
            throw new DomainException(MasterDataErrors.FieldInvalid, "The phone has at most 30 characters.");
        }

        var (emails, error) = PartyEmails.Normalize(command.Emails);
        if (error is not null)
        {
            throw new DomainException(MasterDataErrors.FieldInvalid, error);
        }

        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT is_supplier, version FROM md.party WHERE company_id = @c AND party_id = @p FOR UPDATE",
            r => new Row(r.GetBoolean(0), r.GetInt64(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", command.PartyId)).ConfigureAwait(false);
        if (row is not { IsSupplier: true })
        {
            throw new DomainException(MasterDataErrors.NotFound, "The supplier does not exist.");
        }

        if (row.Version != command.ExpectedVersion)
        {
            throw new DomainException(MasterDataErrors.VersionConflict, $"The supplier changed (version {row.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        var version = command.ExpectedVersion + 1;
        await context.AppendEventAsync(
            new EventDraft("SupplierContactSet", 1, SupplierRules.Aggregate, command.PartyId, version, JsonSerializer.Serialize(new { partyId = command.PartyId, phone, emails }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.party SET phone = @phone, email = @email, version = @v WHERE party_id = @p",
            cancellationToken,
            ("phone", phone),
            ("email", emails.Count > 0 ? emails[0] : null),
            ("v", version),
            ("p", command.PartyId)).ConfigureAwait(false);
        await PartyEmails.ReplaceAsync(context.Connection, context.Transaction, context.CompanyId, command.PartyId, emails, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyId = command.PartyId, phone, emails, version });
    }
}
