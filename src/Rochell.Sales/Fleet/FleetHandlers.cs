using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Fleet;

internal static class FleetRows
{
    public sealed record Table(string Name, string IdColumn, string Aggregate, string Noun);

    public static readonly Table Vehicles = new("log.vehicle", "vehicle_id", "Vehicle", "vehicle");
    public static readonly Table Drivers = new("log.driver", "driver_id", "Driver", "driver");

    public static async Task<(string Status, long Version)> LockAsync(CommandContext context, Table table, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            $"SELECT status, version FROM {table.Name} WHERE company_id = @c AND {table.IdColumn} = @id FOR UPDATE",
            ("c", context.CompanyId),
            ("id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(SalesErrors.NotFound, $"The {table.Noun} does not exist.");
        }

        var (status, version) = (reader.GetString(0), reader.GetInt64(1));
        return version == expectedVersion
            ? (status, version)
            : throw new DomainException(SalesErrors.VersionConflict, $"The {table.Noun} changed (version {version}, expected {expectedVersion}); reload and retry.");
    }

    public static async Task<string> ChangeStatusAsync(CommandContext context, Table table, Guid id, long expectedVersion, string from, string to, string commandType, CancellationToken cancellationToken)
    {
        var (status, version) = await LockAsync(context, table, id, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (status != from)
        {
            throw new DomainException(SalesErrors.InvalidState, $"The {table.Noun} is {status}.");
        }

        var next = version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(table.Aggregate + (to == "ACTIVE" ? "Activated" : "Deactivated"), 1, table.Aggregate, id, next, JsonSerializer.Serialize(new { id, status = to }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, $"UPDATE {table.Name} SET status = @s, version = @v WHERE {table.IdColumn} = @id", cancellationToken, ("s", to), ("v", next), ("id", id)).ConfigureAwait(false);
        await context.AppendStateAsync(table.Aggregate, id, "DOCUMENT", from, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { id, status = to, version = next });
    }

    public static string Plate(string? plate)
    {
        var normalized = new string((plate ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return normalized.Length is >= 5 and <= 10 && normalized.All(char.IsAsciiLetterOrDigit)
            ? normalized
            : throw new DomainException(SalesErrors.PlateInvalid, "The plate has 5 to 10 letters and digits.");
    }

    public static decimal Capacity(decimal kg) => SalesSql.Positive(kg, 6, "The capacity in kg");

    public static string Name(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= 200 ? trimmed : throw new DomainException(SalesErrors.FieldRequired, "The name has 1 to 200 characters.");
    }

    public static string NationalId(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());
        return digits.Length == 11 && digits.All(char.IsAsciiDigit) ? digits : throw new DomainException(SalesErrors.NationalIdInvalid, "The cédula has 11 digits.");
    }
}

[RequiresPermission("fleet:manage")]
public sealed class RegisterVehicleHandler : ICommandHandler<RegisterVehicle>
{
    public string CommandType => "Sales.RegisterVehicle";

    public async Task<string> HandleAsync(RegisterVehicle command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var plate = FleetRows.Plate(command.Plate);
        var capacity = FleetRows.Capacity(command.CapacityKg);
        var eventId = await context.AppendEventAsync(
            new EventDraft("VehicleRegistered", 1, FleetRows.Vehicles.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { vehicleId = context.ResultRef, plate, capacityKg = capacity.ToString(CultureInfo.InvariantCulture) }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO log.vehicle (vehicle_id, company_id, plate, capacity_kg, status, version) VALUES (@id, @c, @plate, @kg, 'ACTIVE', 1)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("plate", plate),
                ("kg", capacity)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(SalesErrors.PlateDuplicate, $"Plate {plate} is already registered.");
        }

        await context.AppendStateAsync(FleetRows.Vehicles.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { vehicleId = context.ResultRef, plate, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("fleet:manage")]
public sealed class UpdateVehicleHandler : ICommandHandler<UpdateVehicle>
{
    public string CommandType => "Sales.UpdateVehicle";

    public async Task<string> HandleAsync(UpdateVehicle command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var capacity = FleetRows.Capacity(command.CapacityKg);
        var (_, version) = await FleetRows.LockAsync(context, FleetRows.Vehicles, command.VehicleId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var next = version + 1;
        await context.AppendEventAsync(
            new EventDraft("VehicleUpdated", 1, FleetRows.Vehicles.Aggregate, command.VehicleId, next,
                JsonSerializer.Serialize(new { vehicleId = command.VehicleId, capacityKg = capacity.ToString(CultureInfo.InvariantCulture) }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE log.vehicle SET capacity_kg = @kg, version = @v WHERE vehicle_id = @id", cancellationToken, ("kg", capacity), ("v", next), ("id", command.VehicleId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { vehicleId = command.VehicleId, version = next });
    }
}

[RequiresPermission("fleet:manage")]
public sealed class DeactivateVehicleHandler : ICommandHandler<DeactivateVehicle>
{
    public string CommandType => "Sales.DeactivateVehicle";

    public Task<string> HandleAsync(DeactivateVehicle command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return FleetRows.ChangeStatusAsync(context, FleetRows.Vehicles, command.VehicleId, command.ExpectedVersion, "ACTIVE", "INACTIVE", CommandType, cancellationToken);
    }
}

[RequiresPermission("fleet:manage")]
public sealed class ActivateVehicleHandler : ICommandHandler<ActivateVehicle>
{
    public string CommandType => "Sales.ActivateVehicle";

    public Task<string> HandleAsync(ActivateVehicle command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return FleetRows.ChangeStatusAsync(context, FleetRows.Vehicles, command.VehicleId, command.ExpectedVersion, "INACTIVE", "ACTIVE", CommandType, cancellationToken);
    }
}

[RequiresPermission("fleet:manage")]
public sealed class RegisterDriverHandler : ICommandHandler<RegisterDriver>
{
    public string CommandType => "Sales.RegisterDriver";

    public async Task<string> HandleAsync(RegisterDriver command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = FleetRows.Name(command.FullName);
        var nationalId = FleetRows.NationalId(command.NationalId);
        var eventId = await context.AppendEventAsync(
            new EventDraft("DriverRegistered", 1, FleetRows.Drivers.Aggregate, context.ResultRef, 1, JsonSerializer.Serialize(new { driverId = context.ResultRef, fullName = name, nationalId }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO log.driver (driver_id, company_id, full_name, national_id, status, version) VALUES (@id, @c, @name, @nid, 'ACTIVE', 1)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("name", name),
                ("nid", nationalId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(SalesErrors.NationalIdDuplicate, $"A driver with cédula {nationalId} is already registered.");
        }

        await context.AppendStateAsync(FleetRows.Drivers.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { driverId = context.ResultRef, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("fleet:manage")]
public sealed class UpdateDriverHandler : ICommandHandler<UpdateDriver>
{
    public string CommandType => "Sales.UpdateDriver";

    public async Task<string> HandleAsync(UpdateDriver command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = FleetRows.Name(command.FullName);
        var (_, version) = await FleetRows.LockAsync(context, FleetRows.Drivers, command.DriverId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var next = version + 1;
        await context.AppendEventAsync(
            new EventDraft("DriverUpdated", 1, FleetRows.Drivers.Aggregate, command.DriverId, next, JsonSerializer.Serialize(new { driverId = command.DriverId, fullName = name }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE log.driver SET full_name = @name, version = @v WHERE driver_id = @id", cancellationToken, ("name", name), ("v", next), ("id", command.DriverId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { driverId = command.DriverId, version = next });
    }
}

[RequiresPermission("fleet:manage")]
public sealed class DeactivateDriverHandler : ICommandHandler<DeactivateDriver>
{
    public string CommandType => "Sales.DeactivateDriver";

    public Task<string> HandleAsync(DeactivateDriver command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return FleetRows.ChangeStatusAsync(context, FleetRows.Drivers, command.DriverId, command.ExpectedVersion, "ACTIVE", "INACTIVE", CommandType, cancellationToken);
    }
}

[RequiresPermission("fleet:manage")]
public sealed class ActivateDriverHandler : ICommandHandler<ActivateDriver>
{
    public string CommandType => "Sales.ActivateDriver";

    public Task<string> HandleAsync(ActivateDriver command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return FleetRows.ChangeStatusAsync(context, FleetRows.Drivers, command.DriverId, command.ExpectedVersion, "INACTIVE", "ACTIVE", CommandType, cancellationToken);
    }
}
