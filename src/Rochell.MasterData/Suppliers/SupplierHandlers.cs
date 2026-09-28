using System.Data.Common;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.MasterData.Suppliers;

internal static class SupplierRules
{
    public const string Aggregate = "Party";

    public static (string Rnc, string LegalName) Validate(IRncRegistry registry, string rnc, string legalName)
    {
        var check = registry.Check(rnc);
        if (!check.IsValid)
        {
            throw new DomainException(MasterDataErrors.RncInvalid, check.Reason ?? "Invalid RNC.");
        }

        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new DomainException(MasterDataErrors.FieldRequired, "The legal name is required.");
        }

        return (check.Normalized!, legalName.Trim());
    }
}

[RequiresPermission("supplier:create")]
public sealed class CreateSupplierHandler(IRncRegistry? registry = null) : ICommandHandler<CreateSupplier>
{
    private readonly IRncRegistry _registry = registry ?? FormatOnlyRncRegistry.Instance;

    public string CommandType => "MasterData.CreateSupplier";

    public async Task<string> HandleAsync(CreateSupplier command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (rnc, legalName) = SupplierRules.Validate(_registry, command.Rnc, command.LegalName);
        var check = _registry.Check(command.Rnc);

        var eventId = await context.AppendEventAsync(
            new EventDraft("SupplierCreated", 1, SupplierRules.Aggregate, context.ResultRef, 1, JsonSerializer.Serialize(new { partyId = context.ResultRef, rnc, legalName, partyKind = "LOCAL" }), Publish: false),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, rnc_validated_at, version)
                VALUES (@id, @company_id, 'LOCAL', @rnc, @legal_name, true, 'DRAFT', @validated_at, 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("company_id", context.CompanyId),
                ("rnc", rnc),
                ("legal_name", legalName),
                ("validated_at", check.ValidatedAt)).ConfigureAwait(false);
        }
        catch (DbException ex) when (MasterRows.IsUniqueViolation(ex))
        {
            throw new DomainException(MasterDataErrors.RncDuplicate, $"A supplier with RNC {rnc} already exists in this company.");
        }

        await context.AppendStateAsync(SupplierRules.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyId = context.ResultRef, status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("supplier:update")]
public sealed class UpdateSupplierHandler(IRncRegistry? registry = null) : ICommandHandler<UpdateSupplier>
{
    private readonly IRncRegistry _registry = registry ?? FormatOnlyRncRegistry.Instance;

    public string CommandType => "MasterData.UpdateSupplier";

    public async Task<string> HandleAsync(UpdateSupplier command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (rnc, legalName) = SupplierRules.Validate(_registry, command.Rnc, command.LegalName);
        await MasterRows.EnsureDraftAtVersionAsync(context, "party", "party_id", command.PartyId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var newVersion = command.ExpectedVersion + 1;

        await context.AppendEventAsync(
            new EventDraft("SupplierUpdated", 1, SupplierRules.Aggregate, command.PartyId, newVersion, JsonSerializer.Serialize(new { partyId = command.PartyId, rnc, legalName }), Publish: false),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE md.party SET rnc = @rnc, legal_name = @legal_name, rnc_validated_at = @validated_at, version = @version WHERE party_id = @id",
                cancellationToken,
                ("rnc", rnc),
                ("legal_name", legalName),
                ("validated_at", _registry.Check(command.Rnc).ValidatedAt),
                ("version", newVersion),
                ("id", command.PartyId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (MasterRows.IsUniqueViolation(ex))
        {
            throw new DomainException(MasterDataErrors.RncDuplicate, $"A supplier with RNC {rnc} already exists in this company.");
        }

        return JsonSerializer.Serialize(new { partyId = command.PartyId, status = "DRAFT", version = newVersion });
    }
}

[RequiresPermission("supplier:activate", StepUp = true)]
public sealed class ActivateSupplierHandler : ICommandHandler<ActivateSupplier>
{
    public string CommandType => "MasterData.ActivateSupplier";

    public async Task<string> HandleAsync(ActivateSupplier command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        await MasterRows.EnsureDraftAtVersionAsync(context, "party", "party_id", command.PartyId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var newVersion = command.ExpectedVersion + 1;

        var eventId = await context.AppendEventAsync(
            new EventDraft("SupplierActivated", 1, SupplierRules.Aggregate, command.PartyId, newVersion, JsonSerializer.Serialize(new { partyId = command.PartyId }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.party SET status = 'ACTIVE', version = @version WHERE party_id = @id",
            cancellationToken,
            ("version", newVersion),
            ("id", command.PartyId)).ConfigureAwait(false);
        await context.AppendStateAsync(SupplierRules.Aggregate, command.PartyId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { partyId = command.PartyId, status = "ACTIVE", version = newVersion });
    }
}

[RequiresPermission("supplier:update")]
public sealed class SetSupplierPaymentTermsHandler : ICommandHandler<SetSupplierPaymentTerms>
{
    public string CommandType => "MasterData.SetSupplierPaymentTerms";

    public async Task<string> HandleAsync(SetSupplierPaymentTerms command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.PaymentTermsDays is < 0 or > 365)
        {
            throw new DomainException(MasterDataErrors.FieldRequired, "Payment terms are 0 to 365 days.");
        }

        await using (var read = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT is_supplier, version FROM md.party WHERE company_id = @c AND party_id = @p FOR UPDATE",
            ("c", context.CompanyId),
            ("p", command.PartyId)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0))
            {
                throw new DomainException(MasterDataErrors.NotFound, "The supplier does not exist.");
            }

            if (reader.GetInt64(1) != command.ExpectedVersion)
            {
                throw new DomainException(MasterDataErrors.VersionConflict, $"The supplier changed (version {reader.GetInt64(1)}, expected {command.ExpectedVersion}); reload and retry.");
            }
        }

        var newVersion = command.ExpectedVersion + 1;
        await context.AppendEventAsync(
            new EventDraft("SupplierPaymentTermsSet", 1, SupplierRules.Aggregate, command.PartyId, newVersion, JsonSerializer.Serialize(new { partyId = command.PartyId, paymentTermsDays = command.PaymentTermsDays }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.party SET supplier_payment_terms_days = @days, version = @version WHERE party_id = @id",
            cancellationToken,
            ("days", command.PaymentTermsDays),
            ("version", newVersion),
            ("id", command.PartyId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyId = command.PartyId, paymentTermsDays = command.PaymentTermsDays, version = newVersion });
    }
}
