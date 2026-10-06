using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.MasterData.Suppliers;

/// <summary>
/// E-USD1-03-9: creates a foreign supplier in DRAFT — legal name, country (ISO 3166-1 alpha-2, e.g. US, CN) and optionally its tax id
/// abroad (1 to 40 characters); no RNC. It is activated like any supplier, and its documents are in USD (E-USD-3).
/// </summary>
public sealed record CreateForeignSupplier(Guid CompanyId, Guid SessionId, string IdempotencyKey, string LegalName, string Country, string? ForeignTaxId = null) : ICommand;

/// <summary>E-USD1-03-9: corrects a DRAFT foreign supplier.</summary>
public sealed record UpdateForeignSupplierDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion, string LegalName, string Country, string? ForeignTaxId = null) : ICommand;

internal static partial class ForeignSupplierRules
{
    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.None, 1000)]
    private static partial Regex CountryCode();

    public static (string LegalName, string Country, string? TaxId) Validate(string? legalName, string? country, string? taxId)
    {
        var name = (legalName ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
        {
            throw new DomainException(MasterDataErrors.FieldRequired, "The legal name has 1 to 200 characters.");
        }

        var code = (country ?? string.Empty).Trim().ToUpperInvariant();
        if (!CountryCode().IsMatch(code))
        {
            throw new DomainException(MasterDataErrors.CountryInvalid, "The country is its two-letter code (US, CN, ES…).");
        }

        var id = string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim();
        return id is { Length: > 40 }
            ? throw new DomainException(MasterDataErrors.FieldInvalid, "The foreign tax id has at most 40 characters.")
            : (name, code, id);
    }

    public static DomainException Duplicate(string country, string? taxId)
        => new(MasterDataErrors.ForeignTaxIdDuplicate, $"A supplier of {country} with tax id {taxId} already exists in this company.");
}

[RequiresPermission("supplier:create")]
public sealed class CreateForeignSupplierHandler : ICommandHandler<CreateForeignSupplier>
{
    public string CommandType => "MasterData.CreateForeignSupplier";

    public async Task<string> HandleAsync(CreateForeignSupplier command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (legalName, country, taxId) = ForeignSupplierRules.Validate(command.LegalName, command.Country, command.ForeignTaxId);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierCreated", 1, SupplierRules.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { partyId = context.ResultRef, legalName, partyKind = "FOREIGN", country, foreignTaxId = taxId }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, version, country, foreign_tax_id)
                VALUES (@id, @c, 'FOREIGN', NULL, @name, true, 'DRAFT', 1, @country, @tax)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("name", legalName),
                ("country", country),
                ("tax", taxId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (MasterRows.IsUniqueViolation(ex))
        {
            throw ForeignSupplierRules.Duplicate(country, taxId);
        }

        await context.AppendStateAsync(SupplierRules.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyId = context.ResultRef, partyKind = "FOREIGN", status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("supplier:update")]
public sealed class UpdateForeignSupplierDraftHandler : ICommandHandler<UpdateForeignSupplierDraft>
{
    public string CommandType => "MasterData.UpdateForeignSupplierDraft";

    public async Task<string> HandleAsync(UpdateForeignSupplierDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (legalName, country, taxId) = ForeignSupplierRules.Validate(command.LegalName, command.Country, command.ForeignTaxId);
        await MasterRows.EnsureDraftAtVersionAsync(context, "party", "party_id", command.PartyId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (await SupplierRules.KindAsync(context, command.PartyId, cancellationToken).ConfigureAwait(false) != "FOREIGN")
        {
            throw new DomainException(MasterDataErrors.SupplierKindMismatch, "A local supplier is corrected with UpdateSupplier.");
        }

        var version = command.ExpectedVersion + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "SupplierUpdated", 1, SupplierRules.Aggregate, command.PartyId, version,
                JsonSerializer.Serialize(new { partyId = command.PartyId, legalName, country, foreignTaxId = taxId }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE md.party SET legal_name = @name, country = @country, foreign_tax_id = @tax, version = @v WHERE party_id = @id",
                cancellationToken,
                ("name", legalName),
                ("country", country),
                ("tax", taxId),
                ("v", version),
                ("id", command.PartyId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (MasterRows.IsUniqueViolation(ex))
        {
            throw ForeignSupplierRules.Duplicate(country, taxId);
        }

        return JsonSerializer.Serialize(new { partyId = command.PartyId, status = "DRAFT", version });
    }
}
