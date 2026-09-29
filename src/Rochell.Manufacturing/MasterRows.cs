using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing;

/// <summary>Machines and shifts (E-MFG1-01-3/4): plant-scoped rows with ACTIVE ⇄ INACTIVE and a version (+1 per change).</summary>
internal static class MasterRows
{
    public sealed record Table(string Name, string IdColumn, string Aggregate, string Noun);

    public static readonly Table Machines = new("md.machine", "machine_id", "Machine", "machine");
    public static readonly Table Shifts = new("mfg.shift", "shift_id", "Shift", "shift");

    /// <summary>Locks the row of the command's plant and checks the expected version.</summary>
    public static async Task<(string Status, long Version)> LockAsync(CommandContext context, Table table, Guid plantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            $"SELECT status, version, plant_id FROM {table.Name} WHERE company_id = @c AND {table.IdColumn} = @id FOR UPDATE",
            ("c", context.CompanyId),
            ("id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ManufacturingErrors.NotFound, $"The {table.Noun} does not exist.");
        }

        var (status, version, plant) = (reader.GetString(0), reader.GetInt64(1), reader.GetGuid(2));
        if (plant != plantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, $"The {table.Noun} belongs to another plant.");
        }

        return version == expectedVersion
            ? (status, version)
            : throw new DomainException(ManufacturingErrors.VersionConflict, $"The {table.Noun} changed (version {version}, expected {expectedVersion}); reload and retry.");
    }

    public static async Task<string> SetStatusAsync(CommandContext context, Table table, Guid plantId, Guid id, long expectedVersion, string? requested, string commandType, CancellationToken cancellationToken)
    {
        var to = requested is "ACTIVE" or "INACTIVE" ? requested : throw new DomainException(ManufacturingErrors.FieldInvalid, "The status is ACTIVE or INACTIVE.");
        var (status, version) = await LockAsync(context, table, plantId, id, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (status == to)
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The {table.Noun} is already {status}.");
        }

        var next = version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(table.Aggregate + (to == "ACTIVE" ? "Activated" : "Deactivated"), 1, table.Aggregate, id, next, JsonSerializer.Serialize(new { id, status = to }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, $"UPDATE {table.Name} SET status = @s, version = @v WHERE {table.IdColumn} = @id", cancellationToken, ("s", to), ("v", next), ("id", id)).ConfigureAwait(false);
        await context.AppendStateAsync(table.Aggregate, id, "DOCUMENT", status, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { id, status = to, version = next });
    }
}
