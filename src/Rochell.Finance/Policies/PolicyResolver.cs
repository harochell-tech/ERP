using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Finance.Policies;

public static class PolicyCodes
{
    public const string Purchasing = "PURCHASING";
    public const string Inventory = "INVENTORY";
    public const string Posting = "POSTING";
}

public static class PolicyParameters
{
    public const string ReceiptTolerancePct = "receipt_tolerance_pct";
    public const string MatchQtyTolerancePct = "match_qty_tolerance_pct";
    public const string MatchPriceTolerancePct = "match_price_tolerance_pct";
    public const string MatchAmountToleranceAbs = "match_amount_tolerance_abs";
    public const string InventoryAdjustmentMateriality = "inventory_adjustment_materiality";
    public const string GrniAgingAlertDays = "grni_aging_alert_days";
    public const string InvoicePriceVarianceAllocationMethod = "invoice_price_variance_allocation_method";
    public const string RoundingDifferenceTolerance = "rounding_difference_tolerance";
    public const string LateEntryHours = "late_entry_hours";
}

/// <summary>The ACTIVE policy version for a date and its parameters (values are strings, E-PR06-5).</summary>
public sealed record ResolvedPolicy(Guid PolicyVersionId, string PolicyCode, int Version, IReadOnlyDictionary<string, string> Values)
{
    public decimal Decimal(string param) => decimal.Parse(Value(param), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public int Integer(string param) => int.Parse(Value(param), NumberStyles.None, CultureInfo.InvariantCulture);

    public string Text(string param) => Value(param);

    private string Value(string param)
        => Values.TryGetValue(param, out var value) ? value : throw new InvalidOperationException($"Policy {PolicyCode} v{Version} has no parameter {param}.");
}

/// <summary>
/// Resolves accounting policies by date inside the command transaction (ADR-039). A missing ACTIVE version is a
/// missing prerequisite (E-PR06-7): the whole command rolls back.
/// </summary>
public static class PolicyResolver
{
    public static async Task<ResolvedPolicy> ResolveAsync(CommandContext context, string policyCode, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Guid? versionId = null;
        var version = 0;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT v.policy_version_id, v.version, p.param_code, p.value #>> '{}'
            FROM acc.accounting_policy_version v
            JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @company AND v.policy_code = @policy AND v.status = 'ACTIVE'
              AND v.effective_from <= @date AND (v.effective_to IS NULL OR v.effective_to > @date)
            """,
            ("company", context.CompanyId),
            ("policy", policyCode),
            ("date", date));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                versionId = reader.GetGuid(0);
                version = reader.GetInt32(1);
                values[reader.GetString(2)] = reader.GetString(3);
            }
        }

        return versionId is null
            ? throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"No ACTIVE {policyCode} accounting policy for {date:yyyy-MM-dd}.")
            : new ResolvedPolicy(versionId.Value, policyCode, version, values);
    }
}
