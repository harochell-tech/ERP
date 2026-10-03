using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// GAS1-01 (E-GAS-1…12, E-GAS-01-1…11): what the database guarantees about purchases of expenses — categories prepared and approved
/// by two people on an expense account that is not a control account, expense lines with category and tax type on documents of
/// their own class, and the seeds (P-37, account roles, the policy parameter, the permissions). No command exists yet, so each case
/// is a block that sets its rows up, runs the statement under test and rolls everything back with a sentinel.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExpensePurchaseSchemaTests(PostgresFixture postgres)
{
    private const string Done = "P0099";

    private sealed record World(TestHarness H, string Setup);

    /// <summary>
    /// The declarations and rows every case starts from: an expense account, a control account, an ACTIVE category «REPARACIONES»
    /// approved by a second user, the tax type ITBIS_18, an ITBIS rule that is not a tax type, an order and an invoice of each class.
    /// </summary>
    private static async Task<World> WorldAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var approver = await h.CreateUserAsync();
        var expense = await h.CreateAccountAsync("63700", "Reparaciones", isControl: false);
        var payable = await h.CreateAccountAsync("20100", "Cuentas por pagar", isControl: true);
        var revenue = await h.CreateAccountAsync("40100", "Ventas", isControl: false);
        await h.AdminRequireAsync($"UPDATE fin.account SET account_class = CASE code WHEN '40100' THEN 'REVENUE' WHEN '20100' THEN 'LIABILITY' ELSE 'EXPENSE' END WHERE company_id = '{h.CompanyId}'");
        var setup = $"""
            DECLARE
              c uuid := '{h.CompanyId}'; u uuid := '{h.UserId}'; u2 uuid := '{approver}'; plant uuid := '{p.PlantId}'; supplier uuid := '{p.SupplierId}'; sand uuid := '{p.Sand}';
              expense uuid := '{expense}'; payable uuid := '{payable}'; revenue uuid := '{revenue}';
              cat uuid := gen_random_uuid(); draft_cat uuid := gen_random_uuid(); tax uuid := gen_random_uuid(); itbis uuid := gen_random_uuid();
              po_exp uuid := gen_random_uuid(); po_inv uuid := gen_random_uuid(); si_exp uuid := gen_random_uuid(); si_inv uuid := gen_random_uuid();
              pol uuid := gen_random_uuid();
              policy uuid := '{p.PolicyVersionId}';
            BEGIN
              INSERT INTO pur.expense_category VALUES (cat, c, 'REPARACIONES', 'Reparaciones', expense, '02', 'SERVICE', 'DRAFT', u, NULL, 1);
              UPDATE pur.expense_category SET status = 'ACTIVE', approved_by = u2, version = 2 WHERE expense_category_id = cat;
              INSERT INTO pur.expense_category VALUES (draft_cat, c, 'LIMPIEZA', 'Limpieza', expense, '02', 'SERVICE', 'DRAFT', u, NULL, 1);
              INSERT INTO tax.fiscal_rule (rule_id, company_id, code, rule_kind) VALUES (tax, c, 'ITBIS_18', 'PURCHASE_TAX_TYPE'), (itbis, c, 'ITBIS_X', 'PURCHASE_ITBIS');
              INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version, doc_class) VALUES
                (po_exp, c, 'OC-2026-000901', supplier, plant, DATE '2026-10-02', 'DRAFT', u, 1, 'EXPENSE'),
                (po_inv, c, 'OC-2026-000902', supplier, plant, DATE '2026-10-02', 'DRAFT', u, 1, 'INVENTORY');
              INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id)
              VALUES (pol, c, po_exp, 1, 10, 1000, 0, 1, 'Reparación de mezcladora', cat, tax);
              INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version,
                                                doc_class, plant_id) VALUES
                (si_exp, c, supplier, 'B0100000901', DATE '2026-10-02', DATE '2026-10-02', 'DRAFT', 'NOT_POSTED', 100, u, 1, 'EXPENSE', plant),
                (si_inv, c, supplier, 'B0100000902', DATE '2026-10-02', DATE '2026-10-02', 'DRAFT', 'NOT_POSTED', 100, u, 1, 'INVENTORY', NULL);
            """;
        return new World(h, setup);
    }

    /// <summary>Runs the setup and <paramref name="statements"/>; the sentinel's state when all of them pass, otherwise the first error's.</summary>
    private static async Task<string?> ProbeAsync(World w, string statements)
        => (await w.H.AdminExecuteAsync($"DO $$ {w.Setup} {statements} RAISE EXCEPTION 'done' USING ERRCODE = '{Done}'; END $$"))?.SqlState;

    private const string ExpenseLine = "INSERT INTO pur.supplier_invoice_line (si_line_id, company_id, si_id, line_no, line_kind, po_line_id, qty, unit_price, net_amount, description, expense_category_id, tax_rule_id) VALUES ";

    [Fact]
    public async Task A_category_approved_by_a_second_person_takes_expense_lines_on_orders_and_invoices_of_expenses()
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(Done, await ProbeAsync(w, ExpenseLine + """
                (gen_random_uuid(), c, si_exp, 1, 'EXPENSE', NULL, 1, 2500, 2500, 'Peaje autopista del Coral', cat, tax),
                (gen_random_uuid(), c, si_exp, 2, 'EXPENSE', pol, 6, 1000, 6000, 'Reparación de mezcladora', cat, tax);
                UPDATE pur.purchase_order_line SET qty_invoiced = 10, version = 2 WHERE po_line_id = pol;
                UPDATE pur.expense_category SET status = 'INACTIVE', version = 3 WHERE expense_category_id = cat;
                INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'REPARACIONES', 'Reparaciones de planta', expense, '02', 'SERVICE', 'DRAFT', u, NULL, 1);
                """));
        }
    }

    [Theory]
    // E-GAS-01-4: the approver is not who prepared.
    [InlineData("UPDATE pur.expense_category SET status = 'ACTIVE', approved_by = u, version = 2 WHERE expense_category_id = draft_cat;", SqlStates.CheckViolation)]
    // E-GAS-2: an ACTIVE expense account that is not a control account.
    [InlineData("INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'PROVEEDORES', 'Proveedores', payable, '02', 'SERVICE', 'DRAFT', u, NULL, 1);", SqlStates.RaiseException)]
    [InlineData("INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'VENTAS', 'Ventas', revenue, '02', 'SERVICE', 'DRAFT', u, NULL, 1);", SqlStates.RaiseException)]
    [InlineData("INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'OTRA', 'Otra', expense, '02', 'SERVICE', 'ACTIVE', u, u2, 1);", SqlStates.RaiseException)]
    // E-GAS-01-3: the account never changes; name, 606 type and class only while DRAFT.
    [InlineData("UPDATE pur.expense_category SET account_id = revenue, version = 2 WHERE expense_category_id = draft_cat;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.expense_category SET goods_type_606 = '09', version = 3 WHERE expense_category_id = cat;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.expense_category SET status = 'DRAFT', version = 3 WHERE expense_category_id = cat;", SqlStates.RaiseException)]
    [InlineData("DELETE FROM pur.expense_category WHERE expense_category_id = draft_cat;", SqlStates.RaiseException)]
    [InlineData("INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'REPARACIONES', 'Otra vez', expense, '02', 'SERVICE', 'DRAFT', u, NULL, 1);", SqlStates.UniqueViolation)]
    [InlineData("INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'OTRA', 'Otra', expense, '12', 'SERVICE', 'DRAFT', u, NULL, 1);", SqlStates.CheckViolation)]
    [InlineData("INSERT INTO pur.expense_category VALUES (gen_random_uuid(), c, 'OTRA', 'Otra', expense, '02', 'ASSET', 'DRAFT', u, NULL, 1);", SqlStates.CheckViolation)]
    // The technical role of the expense line is never mapped.
    [InlineData("INSERT INTO fin.account_role_map (map_id, company_id, account_role, account_id, effective_from, prepared_by, status) VALUES (gen_random_uuid(), c, 'PURCHASE_EXPENSE', expense, DATE '2026-01-01', u, 'DRAFT');", SqlStates.RaiseException)]
    public async Task Categories_are_guarded(string statement, string expected)
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(expected, await ProbeAsync(w, statement));
        }
    }

    [Theory]
    // E-GAS-01-1: a document is of inventory or of expenses.
    [InlineData("INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id) VALUES (gen_random_uuid(), c, po_inv, 1, 1, 100, 0, 1, 'Peaje', cat, tax);", SqlStates.RaiseException)]
    [InlineData("INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, item_id, uom, qty_ordered, unit_price, receipt_tolerance_pct, version) VALUES (gen_random_uuid(), c, po_exp, 2, sand, 't', 1, 100, 0, 1);", SqlStates.RaiseException)]
    // An expense line: ACTIVE category, a tax type, no unit (E-GAS-01-2), a description, never received, billed up to what was ordered.
    [InlineData("INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id) VALUES (gen_random_uuid(), c, po_exp, 2, 1, 100, 0, 1, 'Limpieza', draft_cat, tax);", SqlStates.RaiseException)]
    [InlineData("INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id) VALUES (gen_random_uuid(), c, po_exp, 2, 1, 100, 0, 1, 'Peaje', cat, itbis);", SqlStates.RaiseException)]
    [InlineData("INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, uom, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id) VALUES (gen_random_uuid(), c, po_exp, 2, 't', 1, 100, 0, 1, 'Peaje', cat, tax);", SqlStates.CheckViolation)]
    [InlineData("INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id) VALUES (gen_random_uuid(), c, po_exp, 2, 1, 100, 0, 1, '  ', cat, tax);", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.purchase_order_line SET qty_received = 1, version = 2 WHERE po_line_id = pol;", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.purchase_order_line SET qty_invoiced = 11, version = 2 WHERE po_line_id = pol;", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.purchase_order_line SET description = 'Otra cosa', version = 2 WHERE po_line_id = pol;", SqlStates.RaiseException)]
    // E-GAS-01-10: an expense order is never received.
    [InlineData("UPDATE pur.purchase_order SET status = 'PENDING_APPROVAL', version = 2 WHERE po_id = po_exp; UPDATE pur.purchase_order SET status = 'APPROVED', approved_by = u2, approved_at = now(), policy_version_id = policy, version = 3 WHERE po_id = po_exp; UPDATE pur.purchase_order SET status = 'RECEIVED', version = 4 WHERE po_id = po_exp;", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.purchase_order SET status = 'PENDING_APPROVAL', version = 2 WHERE po_id = po_inv; UPDATE pur.purchase_order SET status = 'APPROVED', approved_by = u2, approved_at = now(), policy_version_id = policy, version = 3 WHERE po_id = po_inv; UPDATE pur.purchase_order SET status = 'CLOSED', version = 4 WHERE po_id = po_inv;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.purchase_order SET doc_class = 'INVENTORY', version = 2 WHERE po_id = po_exp;", SqlStates.RaiseException)]
    public async Task Order_lines_are_of_the_class_of_their_order(string statement, string expected)
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(expected, await ProbeAsync(w, statement));
        }
    }

    [Fact]
    public async Task An_expense_order_is_closed_from_approved_and_reopened()
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(Done, await ProbeAsync(w, """
                UPDATE pur.purchase_order SET status = 'PENDING_APPROVAL', version = 2 WHERE po_id = po_exp;
                UPDATE pur.purchase_order SET status = 'APPROVED', approved_by = u2, approved_at = now(), policy_version_id = policy, version = 3 WHERE po_id = po_exp;
                UPDATE pur.purchase_order SET status = 'CLOSED', version = 4 WHERE po_id = po_exp;
                UPDATE pur.purchase_order SET status = 'APPROVED', version = 5 WHERE po_id = po_exp;
                """));
        }
    }

    [Theory]
    // E-GAS-01-8: an expense invoice has its plant; an inventory invoice has none.
    [InlineData("INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version, doc_class) VALUES (gen_random_uuid(), c, supplier, 'B0100000903', DATE '2026-10-02', DATE '2026-10-02', 'DRAFT', 'NOT_POSTED', 100, u, 1, 'EXPENSE');", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.supplier_invoice SET plant_id = plant, version = 2 WHERE si_id = si_inv;", SqlStates.RaiseException)]
    // E-GAS-01-1: lines of the invoice's class.
    [InlineData(ExpenseLine + "(gen_random_uuid(), c, si_inv, 1, 'EXPENSE', NULL, 1, 100, 100, 'Peaje', cat, tax);", SqlStates.RaiseException)]
    [InlineData(ExpenseLine + "(gen_random_uuid(), c, si_exp, 1, 'INVENTORY_PO', pol, 1, 1000, 1000, NULL, NULL, NULL);", SqlStates.RaiseException)]
    // An expense line: description, ACTIVE category, a tax type; with an order line, the order's own category and tax type.
    [InlineData(ExpenseLine + "(gen_random_uuid(), c, si_exp, 1, 'EXPENSE', NULL, 1, 100, 100, NULL, cat, tax);", SqlStates.CheckViolation)]
    [InlineData(ExpenseLine + "(gen_random_uuid(), c, si_exp, 1, 'EXPENSE', NULL, 1, 100, 100, 'Limpieza', draft_cat, tax);", SqlStates.RaiseException)]
    [InlineData(ExpenseLine + "(gen_random_uuid(), c, si_exp, 1, 'EXPENSE', NULL, 1, 100, 100, 'Peaje', cat, itbis);", SqlStates.RaiseException)]
    [InlineData("INSERT INTO tax.fiscal_rule (rule_id, company_id, code, rule_kind) VALUES ('00000000-0000-0000-0000-0000000000aa', c, 'EXENTO', 'PURCHASE_TAX_TYPE'); "
        + ExpenseLine + "(gen_random_uuid(), c, si_exp, 1, 'EXPENSE', pol, 1, 1000, 1000, 'Reparación', cat, '00000000-0000-0000-0000-0000000000aa');", SqlStates.RaiseException)]
    [InlineData(ExpenseLine + "(gen_random_uuid(), c, si_exp, 1, 'SERVICE_PO', NULL, 1, 100, 100, 'Peaje', cat, tax);", SqlStates.RaiseException)]
    public async Task Invoice_lines_are_of_the_class_of_their_invoice(string statement, string expected)
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(expected, await ProbeAsync(w, statement));
        }
    }

    [Fact]
    public async Task The_application_role_changes_only_what_a_category_may_change()
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal("42501", (await w.H.AppExecuteAsync("UPDATE pur.expense_category SET account_id = account_id"))?.SqlState);
            Assert.Equal("42501", (await w.H.AppExecuteAsync("DELETE FROM pur.expense_category"))?.SqlState);
            Assert.Null(await w.H.AppExecuteAsync("UPDATE pur.expense_category SET name = name, version = version + 1"));
        }
    }

    [Fact]
    public async Task The_seeds_are_the_rule_P37_in_draft_its_roles_the_policy_parameter_and_the_permissions()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(
            "DRAFT|AP-REC|P37-DR-EXP:PURCHASE_EXPENSE,P37-DR-ITBIS:ITBIS_RECOVERABLE,P37-DR-ISC:SELECTIVE_TAX_EXPENSE,P37-DR-OTHER:OTHER_TAX_EXPENSE,P37-DR-TIP:LEGAL_TIP_EXPENSE,P37-CR-AP:AP_CONTROL,P37-CR-WHT:WITHHOLDING_PAYABLE",
            await h.ScalarAsync<string>(
                """
                SELECT v.status || '|' || v.close_component || '|' || (SELECT string_agg((l.line ->> 'code') || ':' || (l.line ->> 'account_role'), ',' ORDER BY l.n)
                                                                    FROM jsonb_array_elements(v.definition -> 'lines') WITH ORDINALITY AS l (line, n))
                FROM fin.posting_rule r JOIN fin.posting_rule_version v USING (posting_rule_id) WHERE r.code = 'P-37'
                """));
        Assert.Equal(
            "LEGAL_TIP_EXPENSE:false,OTHER_TAX_EXPENSE:false,PURCHASE_EXPENSE:false,SELECTIVE_TAX_EXPENSE:false",
            await h.ScalarAsync<string>(
                "SELECT string_agg(role_code || ':' || is_control, ',' ORDER BY role_code) FROM fin.account_role WHERE role_code IN ('SELECTIVE_TAX_EXPENSE', 'OTHER_TAX_EXPENSE', 'LEGAL_TIP_EXPENSE', 'PURCHASE_EXPENSE') AND name IS NOT NULL"));
        Assert.Equal("PURCHASING:AMOUNT", await h.ScalarAsync<string>("SELECT policy_code || ':' || value_type FROM acc.policy_parameter_definition WHERE param_code = 'expense_invoice_approval_threshold'"));
        Assert.Equal(
            "CARGA_CONFIGURACION:expense_category:prepare,CONTADOR:expense_category:prepare,CONTROLLER:expense_category:approve,CONTROLLER:expense_category:prepare", // + the load, E-GAS-03-4
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(r.code || ':' || rp.permission_code, ',' ORDER BY r.code, rp.permission_code)
                FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE rp.permission_code LIKE 'expense_category:%' AND r.code <> 'SUPERADMIN'
                """));
    }
}
