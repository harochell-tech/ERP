using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.MasterData.Company;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.MasterData.Plants;

// PLT-01 (E-PLT-1…5): plants and their locations from Maestros › Plantas y ubicaciones, with company:manage and step-up. A plant is born
// with its valuation area and four locations; plants and locations are never deleted, they are deactivated when nothing is left in them.

/// <summary>E-PLT-1/2: a plant (code 2–20 capitals, digits, «-» or «_», never changed) with its valuation area and RECEPCION, PATIO, CURADO, TRANSITO.</summary>
public sealed record CreatePlant(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, string Name) : ICommand;

/// <summary>E-PLT-2: another location of a plant; CURADO and TRANSITO are the plant's curing and transit locations.</summary>
public sealed record CreateLocation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, string Code, string Name) : ICommand;

/// <summary>E-PLT-2: a location's readable name.</summary>
public sealed record RenameLocation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LocationId, string Name) : ICommand;

/// <summary>E-PLT-3: a plant out of use (no stock, no run in progress, no dispatch on its way) or back in use.</summary>
public sealed record SetPlantStatus(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, bool Active) : ICommand;

/// <summary>E-PLT-3: a location out of use (no stock in it; never CURADO or TRANSITO) or back in use.</summary>
public sealed record SetLocationStatus(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LocationId, bool Active) : ICommand;

public static class PlantErrors
{
    public const string CodeInvalid = "PLANT_CODE_INVALID";
    public const string CodeUsed = "PLANT_CODE_USED";
    public const string InUse = "PLANT_IN_USE";
    public const string LastPlant = "PLANT_LAST_ACTIVE";
    public const string NotFound = "PLANT_NOT_FOUND";
    public const string LocationCodeUsed = "LOCATION_CODE_USED";
    public const string LocationInUse = "LOCATION_IN_USE";
    public const string LocationSystem = "LOCATION_SYSTEM";
    public const string LocationNotFound = "LOCATION_NOT_FOUND";
}

internal static partial class PlantRules
{
    public const string Curing = "CURADO";
    public const string Transit = "TRANSITO";
    public static readonly string[] Standard = ["RECEPCION", "PATIO", Curing, Transit];

    private static readonly Dictionary<string, string> StandardNames = new(StringComparer.Ordinal)
    {
        ["RECEPCION"] = "Recepción de materiales",
        ["PATIO"] = "Patio de producto terminado",
        [Curing] = "Curado",
        [Transit] = "En tránsito al cliente",
    };

    public static string StandardName(string code) => StandardNames[code];

    public static string Code(string? value, int max)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        return code.Length >= 2 && code.Length <= max && CodePattern().IsMatch(code)
            ? code
            : throw new DomainException(PlantErrors.CodeInvalid, $"The code has 2 to {max} capitals, digits, «-» or «_», starting with a letter or digit.");
    }

    public static async Task InsertLocationAsync(CommandContext context, Guid plantId, string code, string name, CancellationToken cancellationToken)
    {
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO md.location (location_id, company_id, plant_id, code, name, is_curing, is_transit) VALUES (@id, @c, @p, @code, @name, @curing, @transit)",
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("p", plantId),
                ("code", code),
                ("name", name),
                ("curing", code == Curing),
                ("transit", code == Transit)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(PlantErrors.LocationCodeUsed, $"The plant already has a location {code}.");
        }
    }

    public static async Task<Guid> PlantEventAsync(CommandContext context, Guid plantId, string type, object payload, CancellationToken cancellationToken)
        => await context.AppendEventAsync(
            new EventDraft(type, 1, "Plant", plantId, await CompanyRules.NextVersionAsync(context, "Plant", plantId, cancellationToken).ConfigureAwait(false), JsonSerializer.Serialize(payload), Publish: true),
            cancellationToken).ConfigureAwait(false);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]*$")]
    private static partial Regex CodePattern();
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class CreatePlantHandler : ICommandHandler<CreatePlant>
{
    private const int CodeMax = 20; // type-limit: md.plant code

    public string CommandType => "MasterData.CreatePlant";

    public async Task<string> HandleAsync(CreatePlant command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = PlantRules.Code(command.Code, CodeMax);
        var name = CompanyRules.Required(command.Name, CompanyRules.PlantNameMax, "plant name");
        var plantId = context.ResultRef;
        var areaId = context.Ids.NewId();
        try
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "INSERT INTO md.valuation_area (company_id, valuation_area_id, code) VALUES (@c, @a, @code)", cancellationToken,
                ("c", context.CompanyId), ("a", areaId), ("code", code)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO md.plant (plant_id, company_id, code, valuation_area_id, name) VALUES (@p, @c, @code, @a, @name)",
                cancellationToken,
                ("p", plantId),
                ("c", context.CompanyId),
                ("code", code),
                ("a", areaId),
                ("name", name)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(PlantErrors.CodeUsed, $"A plant (or valuation area) with the code {code} already exists.");
        }

        foreach (var location in PlantRules.Standard)
        {
            await PlantRules.InsertLocationAsync(context, plantId, location, PlantRules.StandardName(location), cancellationToken).ConfigureAwait(false);
        }

        await PlantRules.PlantEventAsync(context, plantId, "PlantCreated", new { plantId, code, name, valuationAreaId = areaId, locations = PlantRules.Standard }, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { plantId, code, name, locations = PlantRules.Standard });
    }
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class CreateLocationHandler : ICommandHandler<CreateLocation>
{
    private const int CodeMax = 30; // type-limit: md.location code

    public string CommandType => "MasterData.CreateLocation";

    public async Task<string> HandleAsync(CreateLocation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = PlantRules.Code(command.Code, CodeMax);
        var name = CompanyRules.Required(command.Name, CompanyRules.PlantNameMax, "location name");
        var plantCode = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT code FROM md.plant WHERE company_id = @c AND plant_id = @p", r => r.GetString(0), cancellationToken,
            ("c", context.CompanyId), ("p", command.PlantId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(PlantErrors.NotFound, "The plant does not exist.");
        await PlantRules.InsertLocationAsync(context, command.PlantId, code, name, cancellationToken).ConfigureAwait(false);
        await PlantRules.PlantEventAsync(context, command.PlantId, "LocationCreated", new { plantId = command.PlantId, code, name }, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { plantId = command.PlantId, plant = plantCode, code, name });
    }
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class RenameLocationHandler : ICommandHandler<RenameLocation>
{
    public string CommandType => "MasterData.RenameLocation";

    public async Task<string> HandleAsync(RenameLocation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = CompanyRules.Required(command.Name, CompanyRules.PlantNameMax, "location name");
        var location = await Locations.LockAsync(context, command.LocationId, cancellationToken).ConfigureAwait(false);
        await PlantRules.PlantEventAsync(context, location.PlantId, "LocationRenamed", new { locationId = command.LocationId, location.Code, previous = location.Name, name }, cancellationToken)
            .ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.location SET name = @n WHERE location_id = @l", cancellationToken, ("n", name), ("l", command.LocationId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { locationId = command.LocationId, location.Code, name });
    }
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class SetPlantStatusHandler : ICommandHandler<SetPlantStatus>
{
    public string CommandType => "MasterData.SetPlantStatus";

    public async Task<string> HandleAsync(SetPlantStatus command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var plant = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT code, status FROM md.plant WHERE company_id = @c AND plant_id = @p FOR UPDATE", r => (Code: r.GetString(0), Status: r.GetString(1)),
            cancellationToken, ("c", context.CompanyId), ("p", command.PlantId)).ConfigureAwait(false)).SingleOrDefault();
        if (plant == default)
        {
            throw new DomainException(PlantErrors.NotFound, "The plant does not exist.");
        }

        var to = command.Active ? "ACTIVE" : "INACTIVE";
        if (plant.Status == to)
        {
            return JsonSerializer.Serialize(new { plantId = command.PlantId, plant.Code, status = to });
        }

        if (!command.Active)
        {
            var busy = (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT (SELECT count(*) FROM inv.inv_stock_balance b WHERE b.plant_id = @p AND b.quantity <> 0)::int,
                       (SELECT count(*) FROM mfg.production_run r WHERE r.plant_id = @p AND r.status = 'IN_PROGRESS')::int,
                       (SELECT count(*) FROM log.delivery d WHERE d.plant_id = @p AND d.status IN ('PLANNED', 'LOADING', 'LOADED', 'IN_TRANSIT'))::int,
                       (SELECT count(*) FROM md.plant x WHERE x.company_id = @c AND x.status = 'ACTIVE' AND x.plant_id <> @p)::int
                """,
                r => (Stock: r.GetInt32(0), Runs: r.GetInt32(1), Deliveries: r.GetInt32(2), OtherActive: r.GetInt32(3)),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", command.PlantId)).ConfigureAwait(false)).Single();
            if (busy.OtherActive == 0)
            {
                throw new DomainException(PlantErrors.LastPlant, $"{plant.Code} is the only plant in use.");
            }

            if (busy.Stock + busy.Runs + busy.Deliveries > 0)
            {
                throw new DomainException(
                    PlantErrors.InUse,
                    $"{plant.Code} still has {busy.Stock} stock balance(s), {busy.Runs} run(s) in progress and {busy.Deliveries} dispatch(es) on their way (E-PLT-3).");
            }
        }

        await PlantRules.PlantEventAsync(context, command.PlantId, command.Active ? "PlantReactivated" : "PlantDeactivated", new { plantId = command.PlantId, plant.Code }, cancellationToken)
            .ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.plant SET status = @s WHERE plant_id = @p", cancellationToken, ("s", to), ("p", command.PlantId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { plantId = command.PlantId, plant.Code, status = to });
    }
}

[RequiresPermission("company:manage", StepUp = true)]
public sealed class SetLocationStatusHandler : ICommandHandler<SetLocationStatus>
{
    public string CommandType => "MasterData.SetLocationStatus";

    public async Task<string> HandleAsync(SetLocationStatus command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var location = await Locations.LockAsync(context, command.LocationId, cancellationToken).ConfigureAwait(false);
        var to = command.Active ? "ACTIVE" : "INACTIVE";
        if (location.Status == to)
        {
            return JsonSerializer.Serialize(new { locationId = command.LocationId, location.Code, status = to });
        }

        if (!command.Active)
        {
            if (location.Code is PlantRules.Curing or PlantRules.Transit)
            {
                throw new DomainException(PlantErrors.LocationSystem, $"{location.Code} is the plant's own curing or transit location; it is deactivated with the plant.");
            }

            var stock = (await Reading.ListAsync(
                context.Connection, context.Transaction, "SELECT count(*)::int FROM inv.inv_stock_balance WHERE location_id = @l AND quantity <> 0", r => r.GetInt32(0), cancellationToken,
                ("l", command.LocationId)).ConfigureAwait(false)).Single();
            if (stock > 0)
            {
                throw new DomainException(PlantErrors.LocationInUse, $"{location.Code} still holds stock ({stock} balance(s)); move it first (E-PLT-3).");
            }
        }

        await PlantRules.PlantEventAsync(
            context, location.PlantId, command.Active ? "LocationReactivated" : "LocationDeactivated", new { locationId = command.LocationId, location.Code }, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.location SET status = @s WHERE location_id = @l", cancellationToken, ("s", to), ("l", command.LocationId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { locationId = command.LocationId, location.Code, status = to });
    }
}

internal static class Locations
{
    public sealed record Row(Guid PlantId, string Code, string? Name, string Status);

    public static async Task<Row> LockAsync(CommandContext context, Guid locationId, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                "SELECT plant_id, code, name, status FROM md.location WHERE company_id = @c AND location_id = @l FOR UPDATE",
                r => new Row(r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetString(3)),
                cancellationToken,
                ("c", context.CompanyId),
                ("l", locationId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(PlantErrors.LocationNotFound, "The location does not exist.");
}
