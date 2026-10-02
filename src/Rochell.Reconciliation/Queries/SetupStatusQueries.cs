using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Reconciliation.Queries;

/// <summary>
/// E-UX2-9/10: the 19 setup steps in order, each computed from the data (nobody ticks a box): DONE, WARNING (started but incomplete)
/// or PENDING. <see cref="SetupStep.Missing"/> lists what is missing (codes the screen names). Written in SQL over the tables, like
/// the reconciliations (the module graph lets Reconciliation depend on Platform only).
/// </summary>
public sealed record GetSetupStatus(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record SetupStep(int Order, string Code, string Area, string Status, IReadOnlyList<string> Missing);

public sealed record SetupStatus(IReadOnlyList<SetupStep> Steps, bool Complete);

[RequiresPermission("configuration:read")]
public sealed class GetSetupStatusHandler : IQueryHandler<GetSetupStatus>
{
    private const string Steps = """
        WITH today AS (SELECT CAST(@today AS date) AS d),
        fg AS (SELECT item_id, code FROM md.item WHERE company_id = @c AND item_type = 'FINISHED_GOOD' AND status = 'ACTIVE'),
        rule_roles AS (
          SELECT DISTINCT l.line ->> 'account_role' AS role
          FROM fin.posting_rule_version v CROSS JOIN LATERAL jsonb_array_elements(v.definition -> 'lines') AS l (line), today
          WHERE v.status = 'ACTIVE' AND (v.effective_to IS NULL OR v.effective_to > today.d) AND l.line ->> 'account_role' NOT IN ('MANUAL_ADJUSTMENT', 'PURCHASE_EXPENSE')),
        s AS (
          SELECT 1 AS n, 'COMPANY' AS code, 'EMPRESA' AS area, ARRAY[]::text[] AS missing, true AS started
          UNION ALL
          SELECT 2, 'PLANTS', 'EMPRESA',
                 coalesce((SELECT array_agg(code ORDER BY code) FROM md.plant WHERE company_id = @c AND name IS NULL), '{}'),
                 EXISTS (SELECT 1 FROM md.plant WHERE company_id = @c)
          UNION ALL
          SELECT 3, 'USERS', 'SEGURIDAD',
                 ARRAY(SELECT k FROM unnest(ARRAY['CONTROLLER', 'ADMIN_SEGURIDAD', 'SEGUNDO_APROBADOR_SEGURIDAD']) AS k
                       WHERE NOT EXISTS (SELECT 1 FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id
                                         WHERE ra.company_id = @c AND r.code = k AND (ra.valid_to IS NULL OR ra.valid_to > now()))),
                 EXISTS (SELECT 1 FROM iam.role_assignment ra JOIN iam.user u ON u.user_id = ra.user_id WHERE ra.company_id = @c AND u.kind <> 'SERVICE')
          UNION ALL
          SELECT 4, 'PERIODS', 'CONTABILIDAD',
                 CASE WHEN EXISTS (SELECT 1 FROM fin.period, today WHERE company_id = @c AND today.d BETWEEN starts_on AND ends_on) THEN '{}' ELSE ARRAY['TODAY'] END,
                 EXISTS (SELECT 1 FROM fin.period WHERE company_id = @c)
          UNION ALL
          SELECT 5, 'ACCOUNTS', 'CONTABILIDAD',
                 coalesce((SELECT array_agg(code ORDER BY code) FROM fin.account WHERE company_id = @c AND status = 'ACTIVE' AND account_class IS NULL), '{}'),
                 EXISTS (SELECT 1 FROM fin.account WHERE company_id = @c)
          UNION ALL
          SELECT 6, 'REPORT_STRUCTURES', 'CONTABILIDAD',
                 ARRAY(SELECT k FROM unnest(ARRAY['BALANCE_SHEET', 'INCOME_STATEMENT']) AS k
                       WHERE NOT EXISTS (SELECT 1 FROM fin.report_structure_version WHERE company_id = @c AND report = k AND status = 'ACTIVE')),
                 EXISTS (SELECT 1 FROM fin.report_structure_version WHERE company_id = @c)
          UNION ALL
          SELECT 7, 'ACCOUNT_MAPS', 'CONTABILIDAD',
                 ARRAY(SELECT role FROM rule_roles
                       WHERE NOT EXISTS (SELECT 1 FROM fin.account_role_map m, today
                                         WHERE m.company_id = @c AND m.account_role = rule_roles.role AND m.status = 'ACTIVE'
                                           AND m.effective_from <= today.d AND (m.effective_to IS NULL OR m.effective_to > today.d))
                       ORDER BY role),
                 EXISTS (SELECT 1 FROM fin.account_role_map WHERE company_id = @c)
          UNION ALL
          SELECT 8, 'POSTING_RULES', 'CONTABILIDAD',
                 coalesce((SELECT array_agg(r.code ORDER BY r.code) FROM fin.posting_rule r, today
                           WHERE NOT EXISTS (SELECT 1 FROM fin.posting_rule_version v WHERE v.posting_rule_id = r.posting_rule_id AND v.status = 'ACTIVE'
                                               AND v.effective_from <= today.d AND (v.effective_to IS NULL OR v.effective_to > today.d))), '{}'),
                 EXISTS (SELECT 1 FROM fin.posting_rule_version WHERE status = 'ACTIVE')
          UNION ALL
          SELECT 9, 'POLICIES', 'CONTABILIDAD',
                 coalesce((SELECT array_agg(p.policy_code ORDER BY p.policy_code) FROM acc.accounting_policy p, today
                           WHERE NOT EXISTS (
                             SELECT 1 FROM acc.accounting_policy_version v
                             WHERE v.company_id = @c AND v.policy_code = p.policy_code AND v.status = 'ACTIVE'
                               AND v.effective_from <= today.d AND (v.effective_to IS NULL OR v.effective_to > today.d)
                               AND NOT EXISTS (SELECT 1 FROM acc.policy_parameter_definition d
                                               WHERE d.policy_code = p.policy_code
                                                 AND NOT EXISTS (SELECT 1 FROM acc.accounting_policy_parameter x
                                                                 WHERE x.policy_version_id = v.policy_version_id AND x.param_code = d.param_code)))), '{}'),
                 EXISTS (SELECT 1 FROM acc.accounting_policy_version WHERE company_id = @c)
          UNION ALL
          SELECT 10, 'FISCAL_SOURCES', 'FISCAL',
                 CASE WHEN EXISTS (SELECT 1 FROM tax.fiscal_rule_source WHERE company_id = @c AND environment = coalesce(core.current_environment(), environment))
                      THEN '{}' ELSE ARRAY['SOURCE'] END,
                 EXISTS (SELECT 1 FROM tax.fiscal_rule_source WHERE company_id = @c)
          UNION ALL
          SELECT 11, 'FISCAL_RULES', 'FISCAL',
                 ARRAY(SELECT k FROM unnest(ARRAY['PURCHASE_ITBIS', 'SALES_ITBIS', 'PURCHASE_WITHHOLDING', 'REPORT_606_CLASSIFICATION']) AS k
                       WHERE NOT EXISTS (SELECT 1 FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id
                                         WHERE r.company_id = @c AND r.rule_kind = k AND v.status = 'ACTIVE')),
                 EXISTS (SELECT 1 FROM tax.fiscal_rule WHERE company_id = @c)
          UNION ALL
          SELECT 12, 'BANK_ACCOUNTS', 'TESORERIA',
                 CASE WHEN EXISTS (SELECT 1 FROM fin.bank_account WHERE company_id = @c AND status = 'ACTIVE') THEN '{}' ELSE ARRAY['BANK_ACCOUNT'] END, false
          UNION ALL
          SELECT 13, 'SUPPLIERS', 'MAESTROS',
                 CASE WHEN EXISTS (SELECT 1 FROM md.party WHERE company_id = @c AND is_supplier AND status = 'ACTIVE') THEN '{}' ELSE ARRAY['SUPPLIER'] END,
                 EXISTS (SELECT 1 FROM md.party WHERE company_id = @c AND is_supplier)
          UNION ALL
          SELECT 14, 'ITEMS', 'MAESTROS',
                 ARRAY(SELECT k FROM unnest(ARRAY['RAW_MATERIAL', 'FINISHED_GOOD']) AS k
                       WHERE NOT EXISTS (SELECT 1 FROM md.item WHERE company_id = @c AND item_type = k AND status = 'ACTIVE')),
                 EXISTS (SELECT 1 FROM md.item WHERE company_id = @c)
          UNION ALL
          SELECT 15, 'STANDARD_COSTS', 'MAESTROS',
                 CASE WHEN NOT EXISTS (SELECT 1 FROM fg) THEN ARRAY['FINISHED_GOOD']
                      ELSE coalesce((SELECT array_agg(fg.code ORDER BY fg.code) FROM fg
                                     WHERE NOT EXISTS (SELECT 1 FROM md.standard_cost_version c WHERE c.company_id = @c AND c.item_id = fg.item_id AND c.status = 'ACTIVE')), '{}') END,
                 EXISTS (SELECT 1 FROM md.standard_cost_version WHERE company_id = @c)
          UNION ALL
          SELECT 16, 'PRICE_LIST', 'VENTAS',
                 CASE WHEN EXISTS (SELECT 1 FROM sal.price_list_version WHERE company_id = @c AND status = 'ACTIVE') THEN '{}' ELSE ARRAY['PRICE_LIST'] END,
                 EXISTS (SELECT 1 FROM sal.price_list_version WHERE company_id = @c)
          UNION ALL
          SELECT 17, 'CUSTOMERS', 'VENTAS',
                 CASE WHEN NOT EXISTS (SELECT 1 FROM md.party WHERE company_id = @c AND is_customer AND customer_status = 'ACTIVE') THEN ARRAY['CUSTOMER']
                      ELSE coalesce((SELECT array_agg(p.legal_name ORDER BY p.legal_name) FROM md.party p
                                     WHERE p.company_id = @c AND p.is_customer AND p.customer_status = 'ACTIVE'
                                       AND NOT EXISTS (SELECT 1 FROM sal.customer_terms_version t WHERE t.company_id = @c AND t.party_id = p.party_id AND t.status = 'ACTIVE')), '{}') END,
                 EXISTS (SELECT 1 FROM md.party WHERE company_id = @c AND is_customer)
          UNION ALL
          SELECT 18, 'RECIPES', 'PRODUCCION',
                 CASE WHEN NOT EXISTS (SELECT 1 FROM fg) THEN ARRAY['FINISHED_GOOD']
                      ELSE coalesce((SELECT array_agg(fg.code ORDER BY fg.code) FROM fg
                                     WHERE NOT EXISTS (SELECT 1 FROM mfg.recipe_version r WHERE r.company_id = @c AND r.item_id = fg.item_id AND r.status = 'ACTIVE')), '{}') END,
                 EXISTS (SELECT 1 FROM mfg.recipe_version WHERE company_id = @c)
          UNION ALL
          SELECT 19, 'OPENING_INVENTORY', 'INVENTARIO',
                 CASE WHEN EXISTS (SELECT 1 FROM mig.migration_batch WHERE company_id = @c AND kind = 'OPENING_INVENTORY' AND status = 'POSTED') THEN '{}' ELSE ARRAY['BATCH'] END,
                 EXISTS (SELECT 1 FROM mig.migration_batch WHERE company_id = @c))
        SELECT n, code, area,
               CASE WHEN cardinality(missing) = 0 THEN 'DONE' WHEN started THEN 'WARNING' ELSE 'PENDING' END,
               missing
        FROM s ORDER BY n
        """;

    public string QueryType => "Reconciliation.GetSetupStatus";

    public async Task<string> HandleAsync(GetSetupStatus query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var steps = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            Steps,
            r => new SetupStep(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("today", BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))).ConfigureAwait(false);
        return ApiJson.Serialize(new SetupStatus(steps, steps.All(s => s.Status == "DONE")));
    }
}
