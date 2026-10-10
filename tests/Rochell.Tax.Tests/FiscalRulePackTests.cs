using System.Security.Cryptography;
using Npgsql;
using Rochell.Identity;
using Rochell.Platform.Commands;
using Rochell.Tax.Packs;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>
/// CFG-01 (E-CFG-1…6): the configuration load prepares fiscal sources and rules through the application's commands as its own
/// service identity, from the pack and the official documents kept in the repository; it never activates and never overrides.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalRulePackTests(PostgresFixture postgres)
{
    private static readonly Guid Loader = IdentityConstants.ConfigurationLoadUserId;

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rochell.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private static FiscalRulePack Pack() => FiscalRulePack.Parse(File.ReadAllText(Path.Combine(Root(), "deploy", "fiscal", "rules-2026-10.json")));

    private static byte[]? Document(string file)
    {
        var path = Path.Combine(Root(), "docs", "fiscal", "fuentes", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static async Task<Guid> ServiceSessionAsync(TestHarness h)
    {
        await h.GrantAsync(h.CompanyId, Loader, "CARGA_CONFIGURACION");
        return await h.Sessions.StartServiceSessionAsync(Loader);
    }

    private static Task<IReadOnlyList<PackStep>> LoadAsync(TestHarness h, Guid session, Func<string, byte[]?>? read = null)
        => new FiscalRulePackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, Pack(), read ?? Document, FiscalSourceEnvironments.Test, h.Clock.UtcNow.AddSeconds(-1));

    private static string Outcomes(IReadOnlyList<PackStep> steps) => string.Join(',', steps.Select(s => $"{s.Subject}:{s.Outcome}"));

    private static Task<string?> RulesAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(r.code || ':' || v.version || ':' || v.status || ':' || (v.configured_by = '00000000-0000-7000-8000-00000000d003')::text, ',' ORDER BY r.code, v.version)
            FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id
            """);

    [Fact]
    public async Task The_pack_of_the_repository_is_loaded_with_the_fingerprints_of_its_documents_and_left_ready_not_active()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.FiscalActorsAsync();
        var session = await ServiceSessionAsync(h);

        var first = await LoadAsync(h, session);
        var again = await LoadAsync(h, session);

        Assert.Equal("CT-TITULO-III:REGISTERED,NG-07-2018:REGISTERED,ITBIS_COMPRAS:READY,ITBIS_VENTAS:READY,CLASIF_606:READY", Outcomes(first));
        Assert.Equal("CT-TITULO-III:EXISTS,NG-07-2018:EXISTS,ITBIS_COMPRAS:READY,ITBIS_VENTAS:READY,CLASIF_606:READY", Outcomes(again));
        Assert.Equal("CLASIF_606:1:READY:true,ITBIS_COMPRAS:1:READY:true,ITBIS_VENTAS:1:READY:true", await RulesAsync(h));
        Assert.Equal(
            $"Código Tributario (Ley 11-92), Título III — ITBIS:{Convert.ToHexStringLower(SHA256.HashData(Document("titulo3.pdf")!))}:TEST:true," +
            $"Norma General 07-2018 — remisión de informaciones (formatos 606, 607, 608, 609):{Convert.ToHexStringLower(SHA256.HashData(Document("Norma07-18.pdf")!))}:TEST:true",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(document_title || ':' || encode(file_hash, 'hex') || ':' || environment || ':' || (approved_by = '00000000-0000-7000-8000-00000000d003')::text, ',' ORDER BY document_title)
                FROM tax.fiscal_rule_source
                """));
        Assert.Equal((2L, 3L, 2L), (await h.CountAsync("tax.fiscal_rule_source"), await h.CountAsync("tax.fiscal_rule_version"), await h.CountAsync("tax.fiscal_rule_test_run")));
    }

    /// <summary>
    /// X1-01 (E-X1-01-5): the X-1 pack carries four dated versions of «Seguro de vida», the consumer identification amount and the sales
    /// ITBIS that exempts freight (E-X1-1). Until a
    /// person saves the two DGII documents the site will not hand to a program, only what the Code backs is prepared.
    /// </summary>
    [Fact]
    public async Task The_X1_pack_prepares_dated_versions_and_waits_for_the_documents_it_lacks()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.FiscalActorsAsync();
        var session = await ServiceSessionAsync(h);
        var pack = FiscalRulePack.Parse(File.ReadAllText(Path.Combine(Root(), "deploy", "fiscal", "x1-2026-10.json")));
        Task<IReadOnlyList<PackStep>> Load(Func<string, byte[]?> read)
            => new FiscalRulePackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, pack, read, FiscalSourceEnvironments.Test, h.Clock.UtcNow.AddSeconds(-1));

        var partial = await Load(Document);
        var complete = await Load(file => Document(file) ?? System.Text.Encoding.UTF8.GetBytes("saved by a person: " + file));

        Assert.Equal(
            "CT-TITULO-III:REGISTERED,CT-TITULO-IV:REGISTERED,AVISO-10-26:MISSING_FILE,ECF-FORMATO-V1:MISSING_FILE,SEGURO_VIDA:READY,SEGURO_VIDA:SKIPPED,SEGURO_VIDA:SKIPPED,SEGURO_VIDA:SKIPPED,IDENTIFICACION_CONSUMIDOR:SKIPPED,ITBIS_VENTAS:READY",
            Outcomes(partial));
        Assert.Equal(
            "CT-TITULO-III:EXISTS,CT-TITULO-IV:EXISTS,AVISO-10-26:REGISTERED,ECF-FORMATO-V1:REGISTERED,SEGURO_VIDA:READY,SEGURO_VIDA:READY,SEGURO_VIDA:READY,SEGURO_VIDA:READY,IDENTIFICACION_CONSUMIDOR:READY,ITBIS_VENTAS:READY",
            Outcomes(complete));
        Assert.Equal(
            "IDENTIFICACION_CONSUMIDOR:1:READY:true,ITBIS_VENTAS:1:READY:true,SEGURO_VIDA:1:READY:true,SEGURO_VIDA:2:READY:true,SEGURO_VIDA:3:READY:true,SEGURO_VIDA:4:READY:true",
            await RulesAsync(h));
    }

    [Fact]
    public async Task What_a_person_configured_is_completed_when_it_matches_and_left_alone_when_it_differs()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var session = await ServiceSessionAsync(h);
        var from = new DateOnly(2026, 10, 1);

        // As on staging: the same ITBIS rules, typed by a person, waiting for their source; and a 606 classification of their own.
        var purchases = await h.ConfigureAsync(actors, "p", "ITBIS_COMPRAS", FiscalRuleKinds.PurchaseItbis, """{"rate": "0.18", "effect": "RECOVERABLE_INPUT", "tax_code": "ITBIS", "exempt_item_categories": []}""", from);
        await h.ConfigureAsync(actors, "c", "CLASIF_606", FiscalRuleKinds.Report606Classification, """{"classes":{"CEMENTO":"02","AGREGADO":"09","ADITIVO":"09","OTRA_MATERIA_PRIMA":"09"}}""", from);

        var steps = await LoadAsync(h, session);

        Assert.Equal("CT-TITULO-III:REGISTERED,NG-07-2018:REGISTERED,ITBIS_COMPRAS:READY,ITBIS_VENTAS:READY,CLASIF_606:DIFFERENT", Outcomes(steps));
        Assert.Equal("CLASIF_606:1:BLOCKED_PENDING_SOURCE:false,ITBIS_COMPRAS:1:READY:false,ITBIS_VENTAS:1:READY:true", await RulesAsync(h));
        Assert.Equal(purchases, await h.ScalarAsync<Guid>("SELECT v.rule_version_id FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id WHERE r.code = 'ITBIS_COMPRAS'"));
    }

    [Fact]
    public async Task Without_its_document_a_source_is_not_registered_and_the_service_identity_cannot_activate()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var session = await ServiceSessionAsync(h);

        var missing = await LoadAsync(h, session, file => file == "titulo3.pdf" ? null : Document(file));
        var complete = await LoadAsync(h, session);
        var sales = await h.ScalarAsync<Guid>("SELECT v.rule_version_id FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id WHERE r.code = 'ITBIS_VENTAS'");
        var byLoader = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, session, "loader", sales), new ActivateFiscalRuleVersionHandler()));
        await h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "person", sales), new ActivateFiscalRuleVersionHandler());

        Assert.Equal("CT-TITULO-III:MISSING_FILE,NG-07-2018:REGISTERED,ITBIS_COMPRAS:SKIPPED,ITBIS_VENTAS:SKIPPED,CLASIF_606:READY", Outcomes(missing));
        Assert.Equal("CT-TITULO-III:REGISTERED,NG-07-2018:EXISTS,ITBIS_COMPRAS:READY,ITBIS_VENTAS:READY,CLASIF_606:READY", Outcomes(complete));
        Assert.Equal(AuthorizationErrors.NotAuthorized, byLoader.Code);
        Assert.Equal("CLASIF_606:1:READY:true,ITBIS_COMPRAS:1:READY:true,ITBIS_VENTAS:1:ACTIVE:true", await RulesAsync(h));
    }

    [Fact]
    public async Task The_load_role_is_only_for_its_service_identity_which_holds_nothing_else()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var person = await h.CreateUserAsync();

        var toPerson = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, person, "CARGA_CONFIGURACION"));
        var otherRole = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, Loader, "CONTROLLER"));
        var crossed = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, Loader, "PROCESO_DIARIO"));
        await h.GrantAsync(h.CompanyId, Loader, "CARGA_CONFIGURACION");

        Assert.All(new[] { toPerson, otherRole, crossed }, e => Assert.Equal("P0001", (e as PostgresException)?.SqlState));
        Assert.Equal("account:manage,expense_category:prepare,fiscal_rule:configure,fiscal_rule_source:register", await h.ScalarAsync<string>( // + E-GAS-03-4/7
            "SELECT string_agg(permission_code, ',' ORDER BY permission_code) FROM iam.role r JOIN iam.role_permission USING (role_id) WHERE r.code = 'CARGA_CONFIGURACION'"));
    }
}
