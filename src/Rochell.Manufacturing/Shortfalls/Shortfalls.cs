using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Manufacturing.Shortfalls;

// VS3-02c (E-VS3-02c-1…4): raw material production consumed without stock — the list (Producción › Materia prima en negativo) and the
// justification that lets the month's inventory close while a shortfall waits for the material.

/// <summary>E-VS3-02c-4: a reason why an open shortfall may stay open at the close; it stops blocking INV-MOV.</summary>
public sealed record JustifyMaterialShortfall(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ShortfallId, string Reason) : ICommand;

[RequiresPermission("shift_summary:post")]
public sealed class JustifyMaterialShortfallHandler : ICommandHandler<JustifyMaterialShortfall>
{
    public string CommandType => "Manufacturing.JustifyMaterialShortfall";

    public async Task<string> HandleAsync(JustifyMaterialShortfall command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length is 0 or > 500)
        {
            throw new DomainException(ManufacturingErrors.ReasonRequired, "Justifying a shortfall needs a reason of up to 500 characters.");
        }

        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, version FROM inv.material_shortfall WHERE company_id = @c AND shortfall_id = @s FOR UPDATE",
            r => (Status: r.GetString(0), Version: r.GetInt64(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", command.ShortfallId)).ConfigureAwait(false)).SingleOrDefault();
        var status = row.Status ?? throw new DomainException(ManufacturingErrors.NotFound, "The shortfall does not exist.");
        if (status != "OPEN")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The shortfall is {status}; only an open one is justified.");
        }

        var by = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft(
                "MaterialShortfallJustified", 1, "MaterialShortfall", command.ShortfallId, row.Version + 1, JsonSerializer.Serialize(new { shortfallId = command.ShortfallId, reason }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE inv.material_shortfall SET justification = @r, justified_by = @by, justified_at = @at, version = version + 1 WHERE shortfall_id = @s",
            cancellationToken,
            ("r", reason),
            ("by", by),
            ("at", context.Clock.UtcNow),
            ("s", command.ShortfallId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { shortfallId = command.ShortfallId, justified = true });
    }
}

/// <summary>The open shortfalls and those of the last 31 days, newest first.</summary>
public sealed record ListMaterialShortfalls(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record MaterialShortfallView(
    Guid ShortfallId, DateOnly BusinessDate, string PlantCode, string LocationCode, string MaterialCode, string MaterialName, string Uom, decimal Qty, decimal SettledQty,
    decimal StdPrice, decimal Value, decimal SettledValue, string Status, string? RunNo, string? Justification, DateTime? JustifiedAt);

/// <summary>Per material and location, what is open: the stock minus the open shortfalls (negative while they wait).</summary>
public sealed record MaterialBalanceView(string PlantCode, string LocationCode, string MaterialCode, string MaterialName, string Uom, decimal Stock, decimal Open, decimal Balance);

public sealed record MaterialShortfallList(IReadOnlyList<MaterialShortfallView> Items, IReadOnlyList<MaterialBalanceView> Balances, int OpenCount, decimal OpenValue, bool Allowed);

[RequiresPermission("production:read")]
public sealed class ListMaterialShortfallsHandler : IQueryHandler<ListMaterialShortfalls>
{
    public string QueryType => "Manufacturing.ListMaterialShortfalls";

    public async Task<string> HandleAsync(ListMaterialShortfalls query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var today = Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.shortfall_id, s.business_date, p.code, l.code, i.code, i.description, i.base_uom, s.qty, s.settled_qty, s.std_price, s.value, s.settled_value, s.status,
                   r.run_no, s.justification, s.justified_at
            FROM inv.material_shortfall s
            JOIN md.plant p ON p.plant_id = s.plant_id JOIN md.location l ON l.location_id = s.location_id JOIN md.item i ON i.item_id = s.item_id
            LEFT JOIN mfg.production_run r ON r.run_id = s.run_id
            WHERE s.company_id = @c AND (s.status = 'OPEN' OR s.business_date >= @since)
            ORDER BY s.status = 'OPEN' DESC, s.business_date DESC, i.code
            """,
            r => new MaterialShortfallView(
                r.GetGuid(0), r.Date(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10),
                r.GetDecimal(11), r.GetString(12), r.NullableString(13), r.NullableString(14), r.IsDBNull(15) ? null : r.GetDateTime(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("since", today.AddDays(-31))).ConfigureAwait(false);
        var balances = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            WITH open AS (SELECT location_id, item_id, sum(qty - settled_qty) AS q FROM inv.material_shortfall WHERE company_id = @c AND status = 'OPEN' GROUP BY location_id, item_id)
            SELECT p.code, l.code, i.code, i.description, i.base_uom,
                   coalesce((SELECT sum(b.quantity) FROM inv.inv_stock_balance b WHERE b.location_id = o.location_id AND b.item_id = o.item_id), 0), o.q
            FROM open o JOIN md.location l ON l.location_id = o.location_id JOIN md.plant p ON p.plant_id = l.plant_id JOIN md.item i ON i.item_id = o.item_id
            ORDER BY p.code, l.code, i.code
            """,
            r => new MaterialBalanceView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(5) - r.GetDecimal(6)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var open = items.Where(i => i.Status == "OPEN").ToList();
        var allowed = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT (x.value #>> '{}')::numeric FROM acc.accounting_policy_version v JOIN acc.accounting_policy_parameter x ON x.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'PRODUCTION' AND v.status = 'ACTIVE' AND x.param_code = 'raw_material_negative_until'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            ORDER BY v.effective_from DESC LIMIT 1
            """,
            r => r.IsDBNull(0) ? 0m : r.GetDecimal(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", today)).ConfigureAwait(false)).SingleOrDefault() >= decimal.Parse(today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        return ApiJson.Serialize(new MaterialShortfallList(items, balances, open.Count, open.Sum(o => o.Value - o.SettledValue), allowed));
    }
}
