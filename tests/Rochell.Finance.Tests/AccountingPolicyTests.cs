using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>Accounting policies (ADR-039, E-PR06-1…7) and the rounding tolerance in the Posting Engine (E-PR05-7).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AccountingPolicyTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static Task<CommandResult> Prepare(TestHarness h, Guid session, string key, string policy, IReadOnlyDictionary<string, string> parameters, DateOnly from)
        => h.RunAsync(new PrepareAccountingPolicyVersion(h.CompanyId, session, key, policy, from, parameters, "Valores aprobados por el Controller"), new PrepareAccountingPolicyVersionHandler());

    private static Task<CommandResult> Approve(TestHarness h, Guid session, string key, Guid version)
        => h.RunAsync(new ApproveAccountingPolicyVersion(h.CompanyId, session, key, version), new ApproveAccountingPolicyVersionHandler());

    [Fact]
    public async Task Controller_prepares_and_a_different_approver_activates()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");

        var draft = await Prepare(h, controller, "p-1", PolicyCodes.Posting, PolicySetup.Posting, Today(h));
        await Approve(h, approver, "a-1", draft.ResultRef);

        Assert.True(await h.ScalarAsync<bool>(
            "SELECT status = 'ACTIVE' AND approved_by IS NOT NULL AND approved_by <> prepared_by AND version = 1 FROM acc.accounting_policy_version WHERE policy_version_id = @v",
            ("v", draft.ResultRef)));
        Assert.Equal("late_entry_hours=24;rounding_difference_tolerance=0.05", await h.ScalarAsync<string>(
            "SELECT string_agg(param_code || '=' || (value #>> '{}'), ';' ORDER BY param_code) FROM acc.accounting_policy_parameter WHERE policy_version_id = @v",
            ("v", draft.ResultRef)));
    }

    [Fact]
    public async Task Preparer_cannot_approve_their_own_version()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER"); // holds prepare and approve
        var draft = await Prepare(h, controller, "p-2", PolicyCodes.Posting, PolicySetup.Posting, Today(h));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Approve(h, controller, "a-2", draft.ResultRef));

        Assert.Equal(FinanceErrors.FourEyes, ex.Code);
    }

    [Fact]
    public async Task Only_authorized_roles_prepare_and_approval_requires_step_up()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");

        var denied = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, buyer, "p-3", PolicyCodes.Posting, PolicySetup.Posting, Today(h)));
        var draft = await Prepare(h, controller, "p-4", PolicyCodes.Posting, PolicySetup.Posting, Today(h));
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Approve(h, approver, "a-4", draft.ResultRef));

        Assert.Equal(AuthorizationErrors.NotAuthorized, denied.Code);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
    }

    public static TheoryData<string, string, string?> InvalidParameters() => new()
    {
        { "PURCHASING", "receipt_tolerance_pct", null },          // missing
        { "PURCHASING", "receipt_tolerance_pct", "1.5" },         // above 100 %
        { "PURCHASING", "receipt_tolerance_pct", "2%" },          // not a number
        { "PURCHASING", "match_amount_tolerance_abs", "-1" },     // negative
        { "PURCHASING", "match_amount_tolerance_abs", "5.00001" }, // more than 4 decimals
        { "PURCHASING", "late_entry_hours", "24" },               // belongs to another policy
        { "INVENTORY", "grni_aging_alert_days", "1.5" },          // not an integer
        { "INVENTORY", "grni_aging_alert_days", "0" },            // below minimum
        { "INVENTORY", "invoice_price_variance_allocation_method", "PROPORTIONAL" }, // not allowed
    };

    [Theory]
    [MemberData(nameof(InvalidParameters))]
    public async Task Invalid_parameters_are_rejected(string policy, string param, string? value)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var parameters = new Dictionary<string, string>(policy == PolicyCodes.Purchasing ? PolicySetup.Purchasing : PolicySetup.Inventory);
        if (value is null)
        {
            parameters.Remove(param);
        }
        else
        {
            parameters[param] = value;
        }

        var ex = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, controller, $"bad-{param}-{value}", policy, parameters, Today(h)));

        Assert.Equal(FinanceErrors.PolicyParametersInvalid, ex.Code);
        Assert.Equal(0L, await h.CountAsync("acc.accounting_policy_version"));
    }

    [Fact]
    public async Task New_version_closes_the_active_one_and_cannot_start_before_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var first = await h.CreateActivePolicyAsync(PolicyCodes.Posting, PolicySetup.Posting, new DateOnly(2030, 1, 1));

        var earlier = await Prepare(h, controller, "p-5", PolicyCodes.Posting, PolicySetup.Posting, new DateOnly(2029, 1, 1));
        var overlap = await Assert.ThrowsAsync<DomainException>(() => Approve(h, approver, "a-5", earlier.ResultRef));
        var later = await Prepare(h, controller, "p-6", PolicyCodes.Posting, PolicySetup.Posting, new DateOnly(2031, 1, 1));
        await Approve(h, approver, "a-6", later.ResultRef);

        Assert.Equal(FinanceErrors.Overlap, overlap.Code);
        Assert.Equal("2031-01-01", await h.ScalarAsync<string>("SELECT effective_to::text FROM acc.accounting_policy_version WHERE policy_version_id = @v", ("v", first)));
    }

    [Fact]
    public async Task Rounding_difference_within_tolerance_goes_to_ROUNDING_DIFFERENCE_with_the_policy_version()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        await h.CreateActiveMapAsync("ROUNDING_DIFFERENCE", await h.CreateAccountAsync("6999", "Diferencias de redondeo", isControl: false));
        var policy = await h.CreateActivePolicyAsync(PolicyCodes.Posting, PolicySetup.Posting);

        var result = await h.RunAsync(new TestPostingCommand(h.CompanyId, h.SessionId, "round", ledger.PlantId, 10.005m, Today(h), CreditAmount: 10.004m), new TestPostingHandler());
        var journal = System.Text.Json.JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("journalId").GetGuid();

        Assert.Equal("T-DR:10.0100:0.0000,T-CR:0.0000:10.0000,R-08:0.0000:0.0100", await h.ScalarAsync<string>(
            "SELECT string_agg(rule_line_code || ':' || debit || ':' || credit, ',' ORDER BY line_no) FROM fin.gl_entry WHERE journal_id = @j", ("j", journal)));
        Assert.Equal(policy.ToString(), await h.ScalarAsync<string>(
            "SELECT determination_inputs ->> 'policy_version_id' FROM fin.gl_entry WHERE journal_id = @j AND rule_line_code = 'R-08'", ("j", journal)));
    }

    [Fact]
    public async Task Difference_above_tolerance_is_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        await h.CreateActiveMapAsync("ROUNDING_DIFFERENCE", await h.CreateAccountAsync("6999", "Diferencias de redondeo", isControl: false));
        await h.CreateActivePolicyAsync(PolicyCodes.Posting, PolicySetup.Posting);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new TestPostingCommand(h.CompanyId, h.SessionId, "too-big", ledger.PlantId, 10m, Today(h), CreditAmount: 9.90m), new TestPostingHandler()));

        Assert.Equal(FinanceErrors.PostingUnbalanced, ex.Code);
    }

    [Theory]
    [InlineData(false, true)]  // no POSTING policy
    [InlineData(true, false)]  // no ROUNDING_DIFFERENCE mapping
    public async Task Rounding_without_policy_or_mapping_is_a_missing_prerequisite(bool withPolicy, bool withMapping)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        if (withMapping)
        {
            await h.CreateActiveMapAsync("ROUNDING_DIFFERENCE", await h.CreateAccountAsync("6999", "Diferencias de redondeo", isControl: false));
        }

        if (withPolicy)
        {
            await h.CreateActivePolicyAsync(PolicyCodes.Posting, PolicySetup.Posting);
        }

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new TestPostingCommand(h.CompanyId, h.SessionId, "no-prereq", ledger.PlantId, 10.005m, Today(h), CreditAmount: 10.004m), new TestPostingHandler()));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
        Assert.Equal(0L, await h.CountAsync("fin.gl_journal"));
    }

    [Fact]
    public async Task Policies_definitions_and_approver_role_are_seeded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal("INVENTORY:3,POSTING:2,PURCHASING:4", await h.ScalarAsync<string>(
            "SELECT string_agg(policy_code || ':' || n, ',' ORDER BY policy_code) FROM (SELECT policy_code, count(*) n FROM acc.policy_parameter_definition GROUP BY policy_code) x"));
        Assert.Equal("accounting_policy:approve", await h.ScalarAsync<string>(
            "SELECT string_agg(permission_code, ',') FROM iam.role r JOIN iam.role_permission USING (role_id) WHERE r.code = 'APROBADOR_POLITICAS'"));
    }

    [Fact]
    public async Task Approved_parameters_are_immutable_and_versions_cannot_overlap()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var active = await h.CreateActivePolicyAsync(PolicyCodes.Posting, PolicySetup.Posting);

        var update = await h.AdminExecuteAsync("UPDATE acc.accounting_policy_parameter SET value = '\"9\"'");
        var add = await h.AdminExecuteAsync($"INSERT INTO acc.accounting_policy_parameter VALUES ('{h.CompanyId}', '{active}', 'late_entry_hours', '\"1\"')");
        var overlap = await h.AdminExecuteAsync(
            $"INSERT INTO acc.accounting_policy_version (policy_version_id, policy_code, company_id, version, status, effective_from, prepared_by, approved_by, approved_at, justification) VALUES (gen_random_uuid(), 'POSTING', '{h.CompanyId}', 9, 'ACTIVE', '2030-01-01', '00000000-0000-7000-8000-00000000d001', '{h.UserId}', now(), 'x')");

        Assert.Equal(SqlStates.RaiseException, update?.SqlState);
        Assert.Equal(SqlStates.RaiseException, add?.SqlState);
        Assert.Equal("23P01", overlap?.SqlState);
    }
}
