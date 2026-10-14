using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Manufacturing.Portal;

/// <summary>
/// E-MFG2-18: the PRODUCTION policy's portal_import_from (YYYYMMDD) in force today. The portal's days before it are already in the
/// opening inventory, so the sync opens no run and writes no draft for them; 0 or no policy takes every day read.
/// </summary>
public static class PortalCutoff
{
    public static async Task<bool> BeforeAsync(CommandContext context, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var from = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT (x.value #>> '{}')::numeric FROM acc.accounting_policy_version v JOIN acc.accounting_policy_parameter x ON x.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'PRODUCTION' AND v.status = 'ACTIVE' AND x.param_code = 'portal_import_from'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            ORDER BY v.effective_from DESC LIMIT 1
            """,
            r => r.IsDBNull(0) ? (decimal?)null : r.GetDecimal(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))).ConfigureAwait(false)).SingleOrDefault();
        return from is { } value
            && DateOnly.TryParseExact(((long)value).ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            && day < first;
    }
}
