using System.Data.Common;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Machines;

[RequiresPermission("production_master:manage")]
public sealed class CreateMachineHandler : ICommandHandler<CreateMachine>
{
    public string CommandType => "Manufacturing.CreateMachine";

    public async Task<string> HandleAsync(CreateMachine command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = MfgSql.Code(command.Code, 30, "The machine code");
        var name = MfgSql.Name(command.Name);
        await MfgSql.EnsurePlantAsync(context, command.PlantId, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("MachineCreated", 1, MasterRows.Machines.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { machineId = context.ResultRef, plantId = command.PlantId, code, name }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO md.machine (machine_id, company_id, plant_id, code, name, status, version) VALUES (@id, @c, @p, @code, @name, 'ACTIVE', 1)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("p", command.PlantId),
                ("code", code),
                ("name", name)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ManufacturingErrors.CodeDuplicate, $"Machine {code} already exists.");
        }

        await context.AppendStateAsync(MasterRows.Machines.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { machineId = context.ResultRef, code, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("production_master:manage")]
public sealed class RenameMachineHandler : ICommandHandler<RenameMachine>
{
    public string CommandType => "Manufacturing.RenameMachine";

    public async Task<string> HandleAsync(RenameMachine command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = MfgSql.Name(command.Name);
        var (_, version) = await MasterRows.LockAsync(context, MasterRows.Machines, command.PlantId, command.MachineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var next = version + 1;
        await context.AppendEventAsync(
            new EventDraft("MachineRenamed", 1, MasterRows.Machines.Aggregate, command.MachineId, next, JsonSerializer.Serialize(new { machineId = command.MachineId, name }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.machine SET name = @n, version = @v WHERE machine_id = @id", cancellationToken, ("n", name), ("v", next), ("id", command.MachineId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { machineId = command.MachineId, name, version = next });
    }
}

[RequiresPermission("production_master:manage")]
public sealed class SetMachineStatusHandler : ICommandHandler<SetMachineStatus>
{
    public string CommandType => "Manufacturing.SetMachineStatus";

    public Task<string> HandleAsync(SetMachineStatus command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return MasterRows.SetStatusAsync(context, MasterRows.Machines, command.PlantId, command.MachineId, command.ExpectedVersion, command.Status, CommandType, cancellationToken);
    }
}
