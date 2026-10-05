using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Zones;

/// <summary>E-SRV1-2/9, E-PRS-03-2: a delivery zone (Higüey, Bávaro, Cap Cana…), ACTIVE, by Controller or Crédito without approval.</summary>
public sealed record CreateDeliveryZone(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Name) : ICommand;

/// <summary>E-PRS-03-2: a new name, seen everywhere (documents keep the zone, not its name).</summary>
public sealed record RenameDeliveryZone(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ZoneId, long ExpectedVersion, string Name) : ICommand;

/// <summary>E-PRS-01-6: an inactive zone leaves new documents; the documents that have it keep it.</summary>
public sealed record DeactivateDeliveryZone(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ZoneId, long ExpectedVersion) : ICommand;

public sealed record ReactivateDeliveryZone(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ZoneId, long ExpectedVersion) : ICommand;

public static class ZoneErrors
{
    public const string NameUsed = "ZONE_NAME_USED";
    public const string Inactive = "ZONE_INACTIVE";
}

internal static class Zones
{
    public const string Aggregate = "DeliveryZone";

    public sealed record Row(Guid Id, string Name, string Status, long Version);

    public static string Name(string? raw)
    {
        var name = string.Join(' ', (raw ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length is >= 1 and <= 60 ? name : throw new DomainException(SalesErrors.FieldInvalid, "A zone's name has 1 to 60 characters.");
    }

    public static async Task<Row> LockAsync(CommandContext context, Guid zoneId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT zone_id, name, status, version FROM sal.delivery_zone WHERE company_id = @c AND zone_id = @z FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("z", zoneId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The zone does not exist.");
        return row.Version == expectedVersion ? row : throw new DomainException(SalesErrors.VersionConflict, $"The zone is at version {row.Version}, not {expectedVersion}.");
    }

    public static async Task EnsureFreeAsync(CommandContext context, string name, Guid? self, CancellationToken cancellationToken)
    {
        if (await SalesSql.ScalarAsync<Guid?>(
                context, "SELECT zone_id FROM sal.delivery_zone WHERE company_id = @c AND lower(name) = lower(@n) AND zone_id IS DISTINCT FROM @self", cancellationToken,
                ("c", context.CompanyId), ("n", name), ("self", self)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(ZoneErrors.NameUsed, $"There is already a zone {name}.");
        }
    }

    public static async Task<string> WriteAsync(
        CommandContext context, Row row, string name, string status, string eventType, string commandType, CancellationToken cancellationToken)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.Id, await SalesSql.NextEventVersionAsync(context, Aggregate, row.Id, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { zoneId = row.Id, name, status }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.delivery_zone SET name = @n, status = @s, version = @v WHERE zone_id = @z", cancellationToken,
            ("n", name), ("s", status), ("v", version), ("z", row.Id)).ConfigureAwait(false);
        if (status != row.Status)
        {
            await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, status, commandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { zoneId = row.Id, name, status, version });
    }
}

[RequiresPermission("delivery_zone:manage")]
public sealed class CreateDeliveryZoneHandler : ICommandHandler<CreateDeliveryZone>
{
    public string CommandType => "Sales.CreateDeliveryZone";

    public async Task<string> HandleAsync(CreateDeliveryZone command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = Zones.Name(command.Name);
        await SalesSql.LockAsync(context, "delivery-zone", cancellationToken).ConfigureAwait(false);
        await Zones.EnsureFreeAsync(context, name, null, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft("DeliveryZoneCreated", 1, Zones.Aggregate, context.ResultRef, 1, JsonSerializer.Serialize(new { zoneId = context.ResultRef, name }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "INSERT INTO sal.delivery_zone (zone_id, company_id, name, status, version) VALUES (@z, @c, @n, 'ACTIVE', 1)", cancellationToken,
            ("z", context.ResultRef), ("c", context.CompanyId), ("n", name)).ConfigureAwait(false);
        await context.AppendStateAsync(Zones.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { zoneId = context.ResultRef, name, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("delivery_zone:manage")]
public sealed class RenameDeliveryZoneHandler : ICommandHandler<RenameDeliveryZone>
{
    public string CommandType => "Sales.RenameDeliveryZone";

    public async Task<string> HandleAsync(RenameDeliveryZone command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = Zones.Name(command.Name);
        await SalesSql.LockAsync(context, "delivery-zone", cancellationToken).ConfigureAwait(false);
        var row = await Zones.LockAsync(context, command.ZoneId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        await Zones.EnsureFreeAsync(context, name, row.Id, cancellationToken).ConfigureAwait(false);
        return await Zones.WriteAsync(context, row, name, row.Status, "DeliveryZoneRenamed", CommandType, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("delivery_zone:manage")]
public sealed class DeactivateDeliveryZoneHandler : ICommandHandler<DeactivateDeliveryZone>
{
    public string CommandType => "Sales.DeactivateDeliveryZone";

    public async Task<string> HandleAsync(DeactivateDeliveryZone command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Zones.LockAsync(context, command.ZoneId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        return row.Status == "ACTIVE"
            ? await Zones.WriteAsync(context, row, row.Name, "INACTIVE", "DeliveryZoneDeactivated", CommandType, cancellationToken).ConfigureAwait(false)
            : throw new DomainException(SalesErrors.InvalidState, $"The zone is {row.Status}.");
    }
}

[RequiresPermission("delivery_zone:manage")]
public sealed class ReactivateDeliveryZoneHandler : ICommandHandler<ReactivateDeliveryZone>
{
    public string CommandType => "Sales.ReactivateDeliveryZone";

    public async Task<string> HandleAsync(ReactivateDeliveryZone command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Zones.LockAsync(context, command.ZoneId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        return row.Status == "INACTIVE"
            ? await Zones.WriteAsync(context, row, row.Name, "ACTIVE", "DeliveryZoneReactivated", CommandType, cancellationToken).ConfigureAwait(false)
            : throw new DomainException(SalesErrors.InvalidState, $"The zone is {row.Status}.");
    }
}

/// <summary>E-PRS-03-6: the zones, by name; <paramref name="Status"/> ACTIVE or INACTIVE to filter.</summary>
public sealed record ListDeliveryZones(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record DeliveryZoneView(Guid ZoneId, string Name, string Status, long Version);

public sealed record DeliveryZoneList(IReadOnlyList<DeliveryZoneView> Items);

[RequiresPermission("sales:read")]
public sealed class ListDeliveryZonesHandler : IQueryHandler<ListDeliveryZones>
{
    public string QueryType => "Sales.ListDeliveryZones";

    public async Task<string> HandleAsync(ListDeliveryZones query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT zone_id, name, status, version FROM sal.delivery_zone WHERE company_id = @c AND (@s::text IS NULL OR status = @s) ORDER BY lower(name)",
            r => new DeliveryZoneView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new DeliveryZoneList(items));
    }
}
