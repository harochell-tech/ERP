using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Identity.Tests;

/// <summary>Seeds of §14 / P-8, database-level SoD, guards and row-level security (ADR-013).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class IamSchemaTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";

    private static readonly Dictionary<string, string> ExpectedRoles = new()
    {
        ["ADMIN_SEGURIDAD"] = "iam:read,role:assign,role:revoke",
        ["ALMACENISTA"] = "goods_receipt:post,goods_receipt:read,item:create,master_data:read,purchase_order:read,receipt_correction:create",
        ["ANALISTA_FISCAL"] = "configuration:read,fiscal_report:read,fiscal_rule:configure,fiscal_rule_source:register",
        ["APROBADOR_POLITICAS"] = "accounting_policy:approve,configuration:read,opening_inventory:post,price_list:approve,quote:approve_price,report_structure:approve,sales:read,standard_cost:approve", // E-PR06-4, E-FIN1-01-5, E-VS3-01-11, E-VS3-02b-2, E-QUO1-11
        ["APROBADOR_COMPRAS"] = "master_data:read,purchase_order:approve,purchase_order:approve_over_receipt,purchase_order:read",
        ["AUDITOR"] = "audit:read,bank:read,bank_account_number:read,configuration:read,fiscal_report:read,goods_receipt:read,hash:verify,iam:read,ledger:read,"
            + "master_data:read,payment:read,period:read,production:read,purchase_order:read,reconciliation:read,rnc:read,sales:read,supplier_invoice:read",
        ["COMPRADOR"] = "master_data:read,purchase_order:cancel,purchase_order:create,purchase_order:read,purchase_order:submit,rnc:read,supplier:create,supplier:import,supplier:update",
        ["CONTROLLER"] = "account:manage,account_role_map:approve,account_role_map:prepare,accounting_policy:approve,accounting_policy:prepare,audit:read,bank:read,bank_account:manage,"
            + "bank_account_number:read,bank_charge:recognize,bank_line:unmatch,company:manage,configuration:read,cost_collector:settle,customer_refund:release,customer_terms:approve,"
            + "customer_withholding:reverse,expense_category:approve,expense_category:prepare,fiscal_report:read,goods_receipt:read,goods_receipt:reverse,hash:verify,invoice:void,item:activate,"
            + "journal:repost,"
            + "ledger:read,manual_journal:approve,master_data:read,match_exception:approve,opening_inventory:prepare,party_bank_account:verify,payment:read,"
            + "payment:release,payment:reverse,period:read,period_component:close,period_component:reopen,posting_rule:approve,price_list:prepare,production:read,"
            + "purchase_order:approve,purchase_order:read,receipt:reverse,receipt_correction:approve,reconciliation:read,reconciliation:run,rnc:read,sales:read,"
            + "standard_cost:prepare,supplier:activate,supplier_invoice:read,supplier_invoice:reverse,valuation_residual:approve",
        ["DIRECTOR"] = "audit:read,bank:read,bank_account_number:read,configuration:read,fiscal_report:read,goods_receipt:read,hash:verify,iam:read,ledger:read,"
            + "master_data:read,payment:read,period:read,production:read,purchase_order:read,reconciliation:read,rnc:read,sales:read,supplier_invoice:read", // E-ADM-1 (b): every READ permission
        ["CONTADOR"] = "account_role_map:prepare,configuration:read,expense_category:prepare,fiscal_report:read,ledger:read,manual_journal:prepare,period:read,reconciliation:read", // E-FIN1-01-5, E-FIN1-04-2, E-UX4-13
        ["CUENTAS_POR_PAGAR"] = "bank:read,goods_receipt:read,master_data:read,payment:read,purchase_order:read,rnc:read,supplier_invoice:match,supplier_invoice:post,supplier_invoice:read,"
            + "supplier_invoice:register,supplier_invoice:void",
        ["ESPECIALISTA_FISCAL"] = "configuration:read,fiscal_authorization:suspend,fiscal_authorization:verify,fiscal_report:read,fiscal_rule:activate,sales:read", // + E-FIS1-01-9
        ["PROBADOR"] = "identity:act_as", // E-B03-14, TEST databases only
        ["PROCESO_DIARIO"] = "fiscal_authorization:suspend", // E-FIS1-04-7, the API's daily process only
        ["CARGA_CONFIGURACION"] = "account:manage,expense_category:prepare,fiscal_rule:configure,fiscal_rule_source:register", // E-CFG-1, E-GAS-03-4/7: the deployment CLI's configuration load only
        ["SEGUNDO_APROBADOR_CIERRE"] = "period:read,period_component:second_approve",
        ["SEGUNDO_APROBADOR_SEGURIDAD"] = "iam:read,role:second_approve",
        ["VENDEDOR"] = "cash_sale:create,customer:create,customer:import,customer:update,mail:retry,quote:email,quote:manage,rnc:read,sales:read,sales_order:cancel,sales_order:create", // E-VS3-01-11, E-VS3-03-8, E-QUO1-11
        ["CREDITO"] = "credit:approve,customer:activate,customer_terms:prepare,fiscal_authorization:register,rnc:read,sales:read,sales_order:close",
        ["DESPACHO"] = "delivery:email,delivery:manage,fleet:manage,mail:retry,sales:read", // + e-mail (E-MAIL-01-8)
        ["FACTURACION"] = "credit_note:create,credit_note:issue,delivery:email,fiscal_authorization:register,fiscal_document:record,invoice:create,invoice:issue,mail:retry,proforma:email,proforma:void,sales:read",
        ["CAJA"] = "cash_sale:create,receipt:apply,receipt:record,sales:read", // E-CF1-11: sells for cash and records the payment
        ["COBROS"] = "customer_refund:prepare,customer_withholding:record,mail:retry,receipt:apply,receipt:deposit,receipt:record,sales:read,statement:email",
        ["SUPERVISOR_PRODUCCION"] = "master_data:read,production:read,production_run:manage,recipe:prepare,shift_summary:record", // E-MFG1-01-10
        ["GERENTE_PLANTA"] = "fg_lot:scrap,master_data:read,production:read,production_master:manage,recipe:approve,shift_summary:post",
        ["CALIDAD"] = "fg_lot:release,master_data:read,production:read",
        ["TESORERO"] = "bank:read,bank_line:match,bank_statement:import,party_bank_account:request,payment:prepare,payment:read,payment:void,receipt:bounce", // VS#2 §7
    };

    [Fact]
    public async Task Production_seeds_match_the_frozen_permission_matrix()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(129L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.permission WHERE permission_code NOT LIKE 'test:%'")); // + expense_category:prepare / approve (E-GAS-01-4); // + cash_sale:create (E-CF1-11); // + 4 document e-mail permissions and mail:retry (E-MAIL-01-8, 10); // + proforma:void, customer_refund:prepare / release (E-FIS1b-01-9, 11)
        Assert.Equal(45L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.sod_rule")); // + import ≠ activate for suppliers and customers (E-IMP-01-5)
        foreach (var (role, permissions) in ExpectedRoles)
        {
            Assert.Equal(permissions, await h.ScalarAsync<string>(
                "SELECT string_agg(rp.permission_code, ',' ORDER BY rp.permission_code) FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE r.code = @r",
                ("r", role)));
        }

        Assert.Equal(ExpectedRoles.Count + 1, (int)await h.ScalarAsync<long>("SELECT count(*) FROM iam.role WHERE code <> 'TEST_PINGER'")); // + SUPERADMIN (SuperadminTests)
    }

    [Fact]
    public async Task Every_role_says_what_it_is_for()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.role WHERE (description IS NULL OR btrim(description) = '') AND code <> 'TEST_PINGER'")); // E-UX2-12
    }

    [Fact]
    public async Task No_seeded_role_violates_segregation_of_duties_by_itself()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(0L, await h.ScalarAsync<long>(
            """
            SELECT count(*) FROM iam.sod_rule s
            JOIN iam.role_permission a ON a.permission_code = s.permission_a
            JOIN iam.role_permission b ON b.permission_code = s.permission_b AND b.role_id = a.role_id
            WHERE a.role_id <> (SELECT role_id FROM iam.role WHERE code = 'SUPERADMIN') -- E-ADM-2-3: exempt by design
            """));
    }

    [Trait("Acceptance", "RO-02")]
    [Theory]
    [InlineData("ALMACENISTA", "CUENTAS_POR_PAGAR")]      // SC-02: goods_receipt:post / supplier_invoice:post
    [InlineData("ALMACENISTA", "APROBADOR_COMPRAS")]      // goods_receipt:post / purchase_order:approve_over_receipt
    [InlineData("COMPRADOR", "CONTROLLER")]               // supplier:create / supplier:activate
    [InlineData("CONTROLLER", "CUENTAS_POR_PAGAR")]       // supplier_invoice:reverse / supplier_invoice:post
    [InlineData("COMPRADOR", "AUDITOR")]                  // E-PR03-4 b
    [InlineData("CONTROLLER", "AUDITOR")]                 // E-PR03-4 b: the Auditor role cannot be combined with write permissions
    [InlineData("ADMIN_SEGURIDAD", "COMPRADOR")]          // E-PR03-4 c
    [InlineData("ADMIN_SEGURIDAD", "SEGUNDO_APROBADOR_SEGURIDAD")]
    [InlineData("CONTROLLER", "SEGUNDO_APROBADOR_CIERRE")]
    [InlineData("TESORERO", "CONTROLLER")]                // VS#2 §7: payment:prepare / payment:release, request / verify
    [InlineData("TESORERO", "AUDITOR")]                   // E-PR03-4 b
    public async Task Database_rejects_conflicting_role_combinations(string first, string second)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var user = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, user, first);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => h.GrantAsync(h.CompanyId, user, second));

        Assert.StartsWith("SOD_CONFLICT", ex.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("COMPRADOR", "APROBADOR_COMPRAS")]        // create/approve PO: document-level rule, not role-level
    [InlineData("ANALISTA_FISCAL", "ESPECIALISTA_FISCAL")] // E-PR03-4 a: document-level
    public async Task Allowed_role_combinations_are_accepted(string first, string second)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var user = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, user, first);

        await h.GrantAsync(h.CompanyId, user, second);

        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.role_assignment WHERE user_id = @u", ("u", user)));
    }

    [Fact]
    public async Task Segregation_of_duties_is_evaluated_per_company()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var otherCompany = await h.CreateCompanyAsync();
        var user = await h.CreateUserAsync();

        await h.GrantAsync(h.CompanyId, user, "ALMACENISTA");
        await h.GrantAsync(otherCompany, user, "CUENTAS_POR_PAGAR");

        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.role_assignment WHERE user_id = @u", ("u", user)));
    }

    [Trait("Acceptance", "SC-04")]
    [Theory]
    [InlineData("INSERT INTO iam.user (user_id, kind, employee_id, email, oidc_subject, status) VALUES (gen_random_uuid(), 'HUMAN', NULL, 'x@rochell.com.do', 'sub-x', 'ACTIVE')")]
    [InlineData("INSERT INTO iam.user (user_id, kind, employee_id, email, oidc_subject, status) VALUES (gen_random_uuid(), 'HUMAN', gen_random_uuid(), 'x@rochell.com.do', NULL, 'ACTIVE')")]
    [InlineData("INSERT INTO iam.user (user_id, kind, employee_id, email, oidc_subject, status) VALUES (gen_random_uuid(), 'HUMAN', gen_random_uuid(), 'X@Rochell.com.do', 'sub-y', 'ACTIVE')")]
    public async Task SC04_human_users_need_employee_subject_and_lowercase_email(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(SqlStates.CheckViolation, (await h.AdminExecuteAsync(sql))?.SqlState);
    }

    [Fact]
    public async Task Assignments_cannot_be_deleted_edited_or_revoked_twice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var where = $"WHERE user_id = '{h.UserId}'";

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"DELETE FROM iam.role_assignment {where}"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"UPDATE iam.role_assignment SET granted_by = user_id {where}"))?.SqlState);
        Assert.Null(await h.AdminExecuteAsync($"UPDATE iam.role_assignment SET valid_to = now() + interval '1 hour' {where}"));
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"UPDATE iam.role_assignment SET valid_to = now() + interval '2 hour' {where}"))?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await h.AdminExecuteAsync(
            $"INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, valid_from, granted_by) SELECT gen_random_uuid(), '{h.CompanyId}', '{h.UserId}', role_id, now(), '{h.UserId}' FROM iam.role WHERE code = 'COMPRADOR'"))?.SqlState);
    }

    [Fact]
    public async Task Session_identity_is_immutable_and_sessions_cannot_be_deleted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var where = $"WHERE session_id = '{h.SessionId}'";

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"UPDATE iam.session SET login_at = now() {where}"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"DELETE FROM iam.session {where}"))?.SqlState);
        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync($"UPDATE iam.session SET user_id = user_id {where}"))?.SqlState);
    }

    [Theory]
    [InlineData("INSERT INTO iam.user (user_id, kind, status) VALUES (gen_random_uuid(), 'SERVICE', 'ACTIVE')")]
    [InlineData("UPDATE iam.user SET status = 'ACTIVE'")]
    [InlineData("INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'X', 'x')")]
    [InlineData("INSERT INTO iam.permission VALUES ('x:y', 'WRITE')")]
    [InlineData("DELETE FROM iam.sod_rule")]
    [InlineData("DELETE FROM iam.role_assignment")]
    public async Task Application_role_cannot_change_identity_master_data(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync(sql))?.SqlState);
    }

    [Trait("Acceptance", "TEN-02")]
    [Fact]
    public async Task Row_level_security_isolates_companies_for_the_application_role()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var otherCompany = await h.CreateCompanyAsync();
        await h.GrantAsync(otherCompany, h.UserId, "TEST_PINGER");
        await h.RunAsync(h.Ping("rls-a"), new PingHandler());
        await h.RunAsync(h.Ping("rls-b") with { CompanyId = otherCompany }, new PingHandler());

        Assert.Equal("rls-a", await AppScalarAsync(h, h.CompanyId, "SELECT string_agg(idempotency_key, ',') FROM core.command_log"));
        Assert.Equal("rls-b", await AppScalarAsync(h, otherCompany, "SELECT string_agg(idempotency_key, ',') FROM core.command_log"));
        Assert.Equal(2L, await AppScalarAsync(h, h.CompanyId, "SELECT count(*) FROM core.domain_event"));
        Assert.Equal(1L, await AppScalarAsync(h, h.CompanyId, "SELECT count(*) FROM iam.role_assignment"));

        await using var noTenant = h.App.CreateCommand("SELECT count(*) FROM core.command_log");
        Assert.Equal(0L, await noTenant.ExecuteScalarAsync());

        var crossWrite = await h.AppExecuteAsync(
            $"INSERT INTO core.command_log (company_id, command_id, command_type, idempotency_key, session_id, result_ref) VALUES ('{otherCompany}', gen_random_uuid(), 'X', 'k', '{h.SessionId}', gen_random_uuid())",
            h.CompanyId);
        Assert.Equal(InsufficientPrivilege, crossWrite?.SqlState);
        Assert.Contains("row-level security", crossWrite!.MessageText, StringComparison.Ordinal);
    }

    private static async Task<object?> AppScalarAsync(TestHarness h, Guid company, string sql)
    {
        var (connection, transaction) = await h.OpenAppTransactionAsync(company);
        await using (connection)
        await using (transaction)
        {
#pragma warning disable CA2100 // Test SQL literals.
            await using var command = new Npgsql.NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            return await command.ExecuteScalarAsync();
        }
    }
}
