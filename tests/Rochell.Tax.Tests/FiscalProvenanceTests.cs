using Npgsql;
using Rochell.Identity.Authorization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>
/// P-7 provenance of fiscal rules: FIS-01 (immutable definitions, E-PR19-4), FIS-02 and FIS-03 (a PRODUCTION deployment
/// activates only with a PRODUCTION source, and the environment cannot be spoofed; E-PR19-9, Patch 1.1 correction 3).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalProvenanceTests(PostgresFixture postgres)
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private const string OtherItbis = """{"tax_code":"ITBIS","rate":"0.16","effect":"RECOVERABLE_INPUT"}""";

    private static Task<string?> Status(TestHarness h, Guid version)
        => h.ScalarAsync<string>("SELECT status FROM tax.fiscal_rule_version WHERE rule_version_id = @v", ("v", version));

    /// <summary>A PRODUCTION deployment (what `rochell-migrate init-environment PRODUCTION` does) with the fiscal actors.</summary>
    private static async Task<FiscalActors> ProductionAsync(TestHarness h)
    {
        await h.AdminRequireAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('PRODUCTION', current_user, now())");
        return await h.FiscalActorsAsync(initEnvironment: false);
    }

    /// <summary>Configured, linked to one source of the given environment, and with a passing run in this deployment: READY.</summary>
    private static async Task<Guid> ReadyAsync(TestHarness h, FiscalActors actors, string key, string sourceEnvironment)
    {
        var version = await h.ConfigureAsync(actors, key + "-cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var source = await h.RegisterTestSourceAsync(actors, key + "-src", environment: sourceEnvironment);
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, key + "-lnk", version, source), new LinkFiscalSourceHandler());
        await h.RunAsync(
            new RunFiscalRuleTests(h.CompanyId, actors.Analyst, key + "-tst", version, [TaxSetup.PassingCase(FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition)]),
            new RunFiscalRuleTestsHandler());
        return version;
    }

    [Trait("Acceptance", "FIS-01")]
    [Fact]
    public async Task FIS01_a_definition_never_changes_and_a_new_version_needs_its_own_passing_run()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var first = await h.ActivateRuleAsync(actors, "v1", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);

        var byApplication = await h.AppExecuteAsync($"UPDATE tax.fiscal_rule_version SET definition = '{OtherItbis}'::jsonb WHERE rule_version_id = '{first}'");
        var byOwner = await h.AdminExecuteAsync($"UPDATE tax.fiscal_rule_version SET definition = '{OtherItbis}'::jsonb, row_version = row_version + 1 WHERE rule_version_id = '{first}'");
        var second = await h.ConfigureAsync(actors, "v2-cfg", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, OtherItbis, new DateOnly(2026, 7, 1));
        var source = await h.RegisterTestSourceAsync(actors, "v2-src");
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, "v2-lnk", second, source), new LinkFiscalSourceHandler());
        var withoutRun = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "v2-act-0", second), new ActivateFiscalRuleVersionHandler()));
        await h.RunAsync(new RunFiscalRuleTests(h.CompanyId, actors.Analyst, "v2-tst", second, [TaxSetup.PassingCase(FiscalRuleKinds.PurchaseItbis, OtherItbis)]), new RunFiscalRuleTestsHandler());
        await h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "v2-act", second), new ActivateFiscalRuleVersionHandler());

        Assert.Equal("42501", byApplication?.SqlState);
        Assert.Equal(SqlStates.RaiseException, byOwner?.SqlState);
        Assert.Equal(TaxErrors.VersionNotReady, withoutRun.Code);
        Assert.True(await h.ScalarAsync<bool>("SELECT definition = CAST(@d AS jsonb) FROM tax.fiscal_rule_version WHERE rule_version_id = @v", ("d", TaxSetup.ItbisDefinition), ("v", first)));
        Assert.Equal("ACTIVE", await Status(h, second));
    }

    [Trait("Acceptance", "FIS-02")]
    [Fact]
    public async Task FIS02_in_production_a_version_with_only_TEST_sources_is_not_activated()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await ProductionAsync(h);
        var version = await ReadyAsync(h, actors, "p", FiscalSourceEnvironments.Test);

        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "act-1", version), new ActivateFiscalRuleVersionHandler()));
        var forced = await h.AdminExecuteAsync(
            $"UPDATE tax.fiscal_rule_version SET status = 'ACTIVE', activated_by = '00000000-0000-7000-8000-00000000d001', activated_at = now(), row_version = row_version + 1 WHERE rule_version_id = '{version}'");
        var official = await h.RegisterTestSourceAsync(actors, "oficial", environment: FiscalSourceEnvironments.Production);
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, "lnk-oficial", version, official), new LinkFiscalSourceHandler());
        await h.RunAsync(new RunFiscalRuleTests(h.CompanyId, actors.Analyst, "tst-2", version, [TaxSetup.PassingCase(FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition)]), new RunFiscalRuleTestsHandler());
        await h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "act-2", version), new ActivateFiscalRuleVersionHandler());

        Assert.Equal(TaxErrors.ProductionSourceRequired, refused.Code);
        Assert.Equal(SqlStates.RaiseException, forced?.SqlState);
        Assert.Contains("PRODUCTION source", forced?.MessageText, StringComparison.Ordinal);
        Assert.Equal("ACTIVE", await Status(h, version));
    }

    [Trait("Acceptance", "FIS-03")]
    [Fact]
    public async Task FIS03_a_session_cannot_spoof_the_environment_to_activate_with_TEST_sources()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await ProductionAsync(h);
        var version = await ReadyAsync(h, actors, "s", FiscalSourceEnvironments.Test);

        // Every connection of this pool starts with app.environment = 'TEST', as a session that ran SET app.environment would.
        var spoofing = new NpgsqlConnectionStringBuilder(h.Database.AppConnectionString) { Options = "-c app.environment=TEST", Pooling = false }.ConnectionString;
        await using var spoofed = NpgsqlDataSource.Create(spoofing);
        var update = await SpoofedUpdateAsync(spoofed);
        var pipeline = new CommandPipeline(spoofed, new SqlCommandAuthorizer(h.Options, h.Clock), h.RequestLog, h.Clock);
        var refused = await Assert.ThrowsAsync<DomainException>(() => pipeline.ExecuteAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "act", version), new ActivateFiscalRuleVersionHandler(), Guid.CreateVersion7()));

        Assert.Equal("42501", update);
        Assert.Equal(TaxErrors.ProductionSourceRequired, refused.Code);
        Assert.Equal("PRODUCTION", await h.ScalarAsync<string>("SELECT environment FROM core.deployment_environment"));
        Assert.Equal("READY", await Status(h, version));
    }

    [Fact]
    public async Task A_source_states_TEST_or_PRODUCTION()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();

        var invalid = await Assert.ThrowsAsync<DomainException>(() => h.RegisterTestSourceAsync(actors, "x", environment: "STAGING"));
        var direct = await h.AdminExecuteAsync(
            $"""
            INSERT INTO tax.fiscal_rule_source (source_id, company_id, official_source, document_title, document_version, publication_date, consulted_at,
              effective_from, url_or_reference, file_object_key, file_hash, approved_by, approved_at, environment)
            SELECT gen_random_uuid(), company_id, 'x', 'x', 'v1', '2025-12-01', now(), '2025-12-01', 'x', 'x', sha256('x'), '{h.UserId}', now(), 'STAGING'
            FROM md.company WHERE company_id = '{h.CompanyId}'
            """);

        Assert.Equal(TaxErrors.SourceInvalid, invalid.Code);
        Assert.Equal(SqlStates.CheckViolation, direct?.SqlState);
    }

    private static async Task<string?> SpoofedUpdateAsync(NpgsqlDataSource spoofed)
    {
        await using var connection = await spoofed.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SHOW app.environment", connection);
        Assert.Equal("TEST", await command.ExecuteScalarAsync());
        try
        {
            await using var update = new NpgsqlCommand("UPDATE core.deployment_environment SET environment = 'TEST'", connection);
            await update.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }
}
