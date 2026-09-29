using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Shifts;

internal static class ShiftRules
{
    public static (TimeOnly Starts, TimeOnly Ends) Times(TimeOnly starts, TimeOnly ends)
        => starts != ends && starts.Second == 0 && ends.Second == 0 && starts.Millisecond == 0 && ends.Millisecond == 0
            ? (starts, ends)
            : throw new DomainException(ManufacturingErrors.FieldInvalid, "A shift starts and ends at different times, in whole minutes.");

    public static string Text(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}

[RequiresPermission("production_master:manage")]
public sealed class DefineShiftHandler : ICommandHandler<DefineShift>
{
    public string CommandType => "Manufacturing.DefineShift";

    public async Task<string> HandleAsync(DefineShift command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = MfgSql.Code(command.Code, 20, "The shift code");
        var (starts, ends) = ShiftRules.Times(command.StartsAt, command.EndsAt);
        await MfgSql.EnsurePlantAsync(context, command.PlantId, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("ShiftDefined", 1, MasterRows.Shifts.Aggregate, context.ResultRef, 1,
                JsonSerializer.Serialize(new { shiftId = context.ResultRef, plantId = command.PlantId, code, startsAt = ShiftRules.Text(starts), endsAt = ShiftRules.Text(ends) }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO mfg.shift (shift_id, company_id, plant_id, code, starts_at, ends_at, status, version) VALUES (@id, @c, @p, @code, @s, @e, 'ACTIVE', 1)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("p", command.PlantId),
                ("code", code),
                ("s", starts),
                ("e", ends)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ManufacturingErrors.CodeDuplicate, $"Shift {code} already exists in the plant.");
        }

        await context.AppendStateAsync(MasterRows.Shifts.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { shiftId = context.ResultRef, code, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("production_master:manage")]
public sealed class UpdateShiftTimesHandler : ICommandHandler<UpdateShiftTimes>
{
    public string CommandType => "Manufacturing.UpdateShiftTimes";

    public async Task<string> HandleAsync(UpdateShiftTimes command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (starts, ends) = ShiftRules.Times(command.StartsAt, command.EndsAt);
        var (_, version) = await MasterRows.LockAsync(context, MasterRows.Shifts, command.PlantId, command.ShiftId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var next = version + 1;
        await context.AppendEventAsync(
            new EventDraft("ShiftTimesUpdated", 1, MasterRows.Shifts.Aggregate, command.ShiftId, next,
                JsonSerializer.Serialize(new { shiftId = command.ShiftId, startsAt = ShiftRules.Text(starts), endsAt = ShiftRules.Text(ends) }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE mfg.shift SET starts_at = @s, ends_at = @e, version = @v WHERE shift_id = @id", cancellationToken,
            ("s", starts), ("e", ends), ("v", next), ("id", command.ShiftId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { shiftId = command.ShiftId, startsAt = ShiftRules.Text(starts), endsAt = ShiftRules.Text(ends), version = next });
    }
}

[RequiresPermission("production_master:manage")]
public sealed class SetShiftStatusHandler : ICommandHandler<SetShiftStatus>
{
    public string CommandType => "Manufacturing.SetShiftStatus";

    public Task<string> HandleAsync(SetShiftStatus command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return MasterRows.SetStatusAsync(context, MasterRows.Shifts, command.PlantId, command.ShiftId, command.ExpectedVersion, command.Status, CommandType, cancellationToken);
    }
}
