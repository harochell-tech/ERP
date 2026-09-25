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
        ["ADMIN_SEGURIDAD"] = "role:assign,role:revoke",
        ["ALMACENISTA"] = "goods_receipt:post,goods_receipt:read,item:create,master_data:read,purchase_order:read,receipt_correction:create",
        ["ANALISTA_FISCAL"] = "fiscal_rule:configure,fiscal_rule_source:register",
        ["APROBADOR_POLITICAS"] = "accounting_policy:approve", // E-PR06-4
        ["APROBADOR_COMPRAS"] = "master_data:read,purchase_order:approve,purchase_order:approve_over_receipt,purchase_order:read",
        ["AUDITOR"] = "audit:read,goods_receipt:read,hash:verify,master_data:read,period:read,purchase_order:read,reconciliation:read,supplier_invoice:read",
        ["COMPRADOR"] = "master_data:read,purchase_order:cancel,purchase_order:create,purchase_order:read,purchase_order:submit,supplier:create,supplier:update",
        ["CONTROLLER"] = "account_role_map:approve,accounting_policy:approve,accounting_policy:prepare,audit:read,goods_receipt:read,goods_receipt:reverse,"
            + "hash:verify,item:activate,journal:repost,master_data:read,match_exception:approve,period:read,period_component:close,period_component:reopen,"
            + "posting_rule:approve,purchase_order:approve,purchase_order:read,receipt_correction:approve,reconciliation:read,reconciliation:run,"
            + "supplier:activate,supplier_invoice:read,supplier_invoice:reverse,valuation_residual:approve",
        ["CUENTAS_POR_PAGAR"] = "goods_receipt:read,master_data:read,purchase_order:read,supplier_invoice:match,supplier_invoice:post,supplier_invoice:read,"
            + "supplier_invoice:register,supplier_invoice:void",
        ["ESPECIALISTA_FISCAL"] = "fiscal_rule:activate",
        ["SEGUNDO_APROBADOR_CIERRE"] = "period:read,period_component:second_approve",
        ["SEGUNDO_APROBADOR_SEGURIDAD"] = "role:second_approve",
    };

    [Fact]
    public async Task Production_seeds_match_the_frozen_permission_matrix()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(44L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.permission WHERE permission_code NOT LIKE 'test:%'"));
        Assert.Equal(16L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.sod_rule"));
        foreach (var (role, permissions) in ExpectedRoles)
        {
            Assert.Equal(permissions, await h.ScalarAsync<string>(
                "SELECT string_agg(rp.permission_code, ',' ORDER BY rp.permission_code) FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE r.code = @r",
                ("r", role)));
        }

        Assert.Equal(ExpectedRoles.Count, (int)await h.ScalarAsync<long>("SELECT count(*) FROM iam.role WHERE code <> 'TEST_PINGER'"));
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
