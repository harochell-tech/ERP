using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>The fiscal activation gate (§9.1, E-PR12-4/5): source, passing tests in this environment, specialist ≠ configurer, step-up.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalGateTests(PostgresFixture postgres)
{
    private static readonly DateOnly From = new(2026, 1, 1);

    private static Task<string?> Status(TestHarness h, Guid version)
        => h.ScalarAsync<string>("SELECT status FROM tax.fiscal_rule_version WHERE rule_version_id = @v", ("v", version));

    [Fact]
    public async Task A_version_becomes_active_only_with_a_source_a_passing_run_and_a_re_authenticated_specialist()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var actors = await h.FiscalActorsAsync();
        var version = await h.ConfigureAsync(actors, "cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var blocked = await Status(h, version);

        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "act-0", version), new ActivateFiscalRuleVersionHandler()));
        var source = await h.RegisterTestSourceAsync(actors, "src");
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, "lnk", version, source), new LinkFiscalSourceHandler());
        var linkedOnly = await Status(h, version);

        var wrong = new FiscalTestCase("caso-malo", PartyTaxTypes.Company, "CEMENTO", 1000m, 0m, [new ExpectedTax("ITBIS", 170m, TaxEffects.RecoverableInput)]);
        var failed = await h.RunAsync(new RunFiscalRuleTests(h.CompanyId, actors.Analyst, "tst-1", version, [wrong]), new RunFiscalRuleTestsHandler());
        var afterFailure = await Status(h, version);
        await h.RunAsync(new RunFiscalRuleTests(h.CompanyId, actors.Analyst, "tst-2", version, [TaxSetup.PassingCase(FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition)]), new RunFiscalRuleTestsHandler());
        var ready = await Status(h, version);

        var analyst = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Analyst, "act-1", version), new ActivateFiscalRuleVersionHandler()));
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "act-2", version), new ActivateFiscalRuleVersionHandler()));
        var fresh = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");
        await h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, fresh, "act-3", version), new ActivateFiscalRuleVersionHandler());

        Assert.Equal("BLOCKED_PENDING_SOURCE", blocked);
        Assert.Equal(TaxErrors.VersionNotReady, early.Code);
        Assert.Equal("BLOCKED_PENDING_SOURCE", linkedOnly);
        Assert.False(JsonDocument.Parse(failed.ResultPayload).RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("BLOCKED_PENDING_SOURCE", afterFailure);
        Assert.Equal("READY", ready);
        Assert.Equal(AuthorizationErrors.NotAuthorized, analyst.Code);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
        Assert.Equal("ACTIVE", await Status(h, version));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM tax.fiscal_rule_test_run WHERE rule_version_id = @v AND environment = 'TEST'", ("v", version)));
    }

    [Fact]
    public async Task A_test_run_without_an_initialized_environment_fails_loudly_and_records_nothing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync(initEnvironment: false);
        var version = await h.ConfigureAsync(actors, "cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.RunAsync(
            new RunFiscalRuleTests(h.CompanyId, actors.Analyst, "tst", version, [TaxSetup.PassingCase(FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition)]),
            new RunFiscalRuleTestsHandler()));

        Assert.Contains("init-environment", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await h.CountAsync("tax.fiscal_rule_test_run"));
    }

    public static TheoryData<string, string> InvalidDefinitions() => new()
    {
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT","extra":1}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"0","effect":"RECOVERABLE_INPUT"}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"1.5","effect":"RECOVERABLE_INPUT"}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"0.1234567","effect":"RECOVERABLE_INPUT"}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":0.18,"effect":"RECOVERABLE_INPUT"}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"0.18","effect":"WITHHOLDING"}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT","exempt_item_categories":["ARENA"]}""" },
        { FiscalRuleKinds.PurchaseItbis, """{"tax_code":"itbis","rate":"0.18","effect":"RECOVERABLE_INPUT"}""" },
        { FiscalRuleKinds.PurchaseWithholding, """{"tax_code":"RET","rate":"0.30","base":"GROSS","party_types":["INDIVIDUAL"]}""" },
        { FiscalRuleKinds.PurchaseWithholding, """{"tax_code":"RET","rate":"0.30","base":"NET","party_types":[]}""" },
        { FiscalRuleKinds.PurchaseWithholding, """{"tax_code":"RET","rate":"0.30","base":"NET","party_types":["FOREIGN"]}""" },
        { FiscalRuleKinds.PurchaseWithholding, """not json""" },
    };

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public async Task Invalid_definitions_are_rejected(string kind, string definition)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.ConfigureAsync(actors, "bad", "REGLA", kind, definition, From));

        Assert.Equal(TaxErrors.FiscalRuleInvalid, ex.Code);
        Assert.Equal(0L, await h.CountAsync("tax.fiscal_rule_version"));
    }

    [Fact]
    public async Task Sources_need_a_real_hash_and_must_be_in_force_and_kinds_never_change()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var version = await h.ConfigureAsync(actors, "cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var lateSource = await h.RegisterTestSourceAsync(actors, "late", effectiveFrom: new DateOnly(2026, 3, 1));

        var badHash = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterFiscalSource(h.CompanyId, actors.Analyst, "hash", "TEST", "Norma", "v1", new DateOnly(2025, 12, 1), h.Clock.UtcNow, new DateOnly(2025, 12, 1), null, "ref", "file", "not-a-hash"),
            new RegisterFiscalSourceHandler()));
        var notInForce = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, "lnk", version, lateSource), new LinkFiscalSourceHandler()));
        var kind = await Assert.ThrowsAsync<DomainException>(() => h.ConfigureAsync(actors, "kind", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseWithholding, TaxSetup.WithholdingDefinition, From));

        Assert.Equal(TaxErrors.SourceInvalid, badHash.Code);
        Assert.Equal(TaxErrors.SourceNotEffective, notInForce.Code);
        Assert.Equal(TaxErrors.RuleKindMismatch, kind.Code);
    }

    [Fact]
    public async Task A_later_version_closes_its_predecessor_and_one_starting_earlier_retires_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var v1 = await h.ActivateRuleAsync(actors, "v1", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var v2 = await h.ActivateRuleAsync(actors, "v2", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, new DateOnly(2026, 7, 1));
        var v3 = await h.ActivateRuleAsync(actors, "v3", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, new DateOnly(2026, 7, 1));

        Assert.Equal("ACTIVE|2026-07-01", await h.ScalarAsync<string>("SELECT status || '|' || effective_to FROM tax.fiscal_rule_version WHERE rule_version_id = @v", ("v", v1)));
        Assert.Equal("RETIRED", await Status(h, v2));
        Assert.Equal("ACTIVE", await Status(h, v3));
    }

    [Fact]
    public async Task Only_one_purchase_ITBIS_rule_applies_at_a_time()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "a", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.ActivateRuleAsync(actors, "b", "ITBIS-OTRA", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From));

        Assert.Equal(TaxErrors.AnotherItbisRuleActive, ex.Code);
    }

    /// <summary>E-PR03-4 (a): one person may hold both roles, but never activates a version they configured.</summary>
    [Fact]
    public async Task A_person_with_both_roles_cannot_activate_a_version_they_configured()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var both = await h.SessionWithRolesAsync("ANALISTA_FISCAL", "ESPECIALISTA_FISCAL");
        var own = new FiscalActors(both, both);
        var version = await h.ConfigureAsync(own, "cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var source = await h.RegisterTestSourceAsync(own, "src");
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, both, "lnk", version, source), new LinkFiscalSourceHandler());
        await h.RunAsync(new RunFiscalRuleTests(h.CompanyId, both, "tst", version, [TaxSetup.PassingCase(FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition)]), new RunFiscalRuleTestsHandler());

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, both, "act", version), new ActivateFiscalRuleVersionHandler()));
        var forced = await h.AdminExecuteAsync(
            $"UPDATE tax.fiscal_rule_version SET status = 'ACTIVE', activated_by = configured_by, activated_at = now(), row_version = row_version + 1 WHERE rule_version_id = '{version}'");

        Assert.Equal(TaxErrors.ActivatorIsConfigurer, ex.Code);
        Assert.Equal(SqlStates.CheckViolation, forced?.SqlState);
        Assert.Equal("READY", await Status(h, version));
    }

    [Theory]
    [InlineData("UPDATE tax.fiscal_rule_version SET status = 'ACTIVE', activated_by = '00000000-0000-7000-8000-00000000d001', activated_at = now(), row_version = row_version + 1")]
    [InlineData("UPDATE tax.fiscal_rule_version SET definition = '{}', row_version = row_version + 1")]
    [InlineData("DELETE FROM tax.fiscal_rule_version")]
    [InlineData("UPDATE tax.fiscal_rule_source SET document_version = 'v2'")]
    public async Task The_gate_holds_in_the_database_even_for_the_owner(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ConfigureAsync(actors, "cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        await h.RegisterTestSourceAsync(actors, "src");

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
    }

    [Theory]
    [InlineData("UPDATE tax.fiscal_rule_version SET definition = definition")]
    [InlineData("DELETE FROM tax.fiscal_rule_test_run")]
    public async Task Application_role_cannot_bypass_the_commands(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "a", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);

        Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState);
    }

    [Fact]
    public async Task Row_level_security_isolates_fiscal_configuration()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "a", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var other = await h.CreateCompanyAsync();
        var (connection, tx) = await h.OpenAppTransactionAsync(other);
        await using (connection)
        await using (tx)
        {
            await using var count = new Npgsql.NpgsqlCommand(
                "SELECT (SELECT count(*) FROM tax.fiscal_rule) + (SELECT count(*) FROM tax.fiscal_rule_version) + (SELECT count(*) FROM tax.fiscal_rule_source)", connection, tx);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
    }
}
