using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.MasterData.Company;

// E-UX2-11: Configuración › Empresa. The RNC identifies the company and never changes (another RNC is another company); the
// legal name and the plants' names are edited from the screen (company:manage, with step-up). Each change is a domain event.

public sealed record UpdateCompanyLegalName(Guid CompanyId, Guid SessionId, string IdempotencyKey, string LegalName) : ICommand;

public sealed record UpdatePlantName(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, string Name) : ICommand;

public sealed record GetCompany(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record CompanyPlantView(Guid PlantId, string Code, string? Name);

public sealed record CompanyView(Guid CompanyId, string Rnc, string LegalName, IReadOnlyList<CompanyPlantView> Plants);

internal static class CompanyRules
{
    public const int LegalNameMax = 200;
    public const int PlantNameMax = 100;

    public static string Required(string? value, int max, string what)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length is 0 || trimmed.Length > max
            ? throw new DomainException(MasterDataErrors.FieldRequired, $"The {what} is required (at most {max} characters).")
            : trimmed;
    }

    public static async Task<long> NextVersionAsync(CommandContext context, string aggregate, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT max(aggregate_version) FROM core.domain_event WHERE company_id = @c AND aggregate_type = @t AND aggregate_id = @a",
            ("c", context.CompanyId),
            ("t", aggregate),
            ("a", id));
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long? ?? 0) + 1;
    }
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class UpdateCompanyLegalNameHandler : ICommandHandler<UpdateCompanyLegalName>
{
    public string CommandType => "MasterData.UpdateCompanyLegalName";

    public async Task<string> HandleAsync(UpdateCompanyLegalName command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var legalName = CompanyRules.Required(command.LegalName, CompanyRules.LegalNameMax, "legal name");
        string previous;
        await using (var read = Sql.Command(context.Connection, context.Transaction, "SELECT legal_name FROM md.company WHERE company_id = @c FOR UPDATE", ("c", context.CompanyId)))
        {
            previous = (string)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await context.AppendEventAsync(
            new EventDraft(
                "CompanyLegalNameChanged",
                1,
                "Company",
                context.CompanyId,
                await CompanyRules.NextVersionAsync(context, "Company", context.CompanyId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { companyId = context.CompanyId, previous, legalName }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.company SET legal_name = @n WHERE company_id = @c", cancellationToken, ("n", legalName), ("c", context.CompanyId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { companyId = context.CompanyId, legalName });
    }
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class UpdatePlantNameHandler : ICommandHandler<UpdatePlantName>
{
    public string CommandType => "MasterData.UpdatePlantName";

    public async Task<string> HandleAsync(UpdatePlantName command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = CompanyRules.Required(command.Name, CompanyRules.PlantNameMax, "plant name");
        string code;
        string? previous;
        await using (var read = Sql.Command(
            context.Connection, context.Transaction, "SELECT code, name FROM md.plant WHERE company_id = @c AND plant_id = @p FOR UPDATE", ("c", context.CompanyId), ("p", command.PlantId)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(MasterDataErrors.NotFound, "The plant does not exist.");
            }

            code = reader.GetString(0);
            previous = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        await context.AppendEventAsync(
            new EventDraft(
                "PlantNameChanged",
                1,
                "Plant",
                command.PlantId,
                await CompanyRules.NextVersionAsync(context, "Plant", command.PlantId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { plantId = command.PlantId, code, previous, name }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.plant SET name = @n WHERE plant_id = @p", cancellationToken, ("n", name), ("p", command.PlantId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { plantId = command.PlantId, code, name });
    }
}

[RequiresPermission("configuration:read")]
public sealed class GetCompanyHandler : IQueryHandler<GetCompany>
{
    public string QueryType => "MasterData.GetCompany";

    public async Task<string> HandleAsync(GetCompany query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var company = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT rnc, legal_name FROM md.company WHERE company_id = @c",
            r => (Rnc: r.GetString(0), LegalName: r.GetString(1)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).Single();
        var plants = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, code, name FROM md.plant WHERE company_id = @c ORDER BY code",
            r => new CompanyPlantView(r.GetGuid(0), r.GetString(1), r.NullableString(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new CompanyView(context.CompanyId, company.Rnc, company.LegalName, plants));
    }
}
