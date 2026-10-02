using System.Text.Json;
using Rochell.Identity;
using Rochell.Platform.Commands;
using Rochell.Tax.Packs;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>
/// GAS1-02 (E-GAS-3, E-GAS-9, E-GAS-02-1…7): the tax type of an expense line — a fiscal rule with a label and components, several
/// in force at once; a line carries only the components of its own type, withholdings may be limited to services, goods or
/// inventory, and only the types a document names can close its gate.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PurchaseTaxTypeTests(PostgresFixture postgres)
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly Day = new(2026, 3, 1);

    private const string Itbis18 = """{"label":"ITBIS 18 %","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"}]}""";
    private const string Itbis16 = """{"label":"ITBIS 16 %","components":[{"tax_code":"ITBIS","rate":"0.16","effect":"RECOVERABLE_INPUT"}]}""";
    private const string Exempt = """{"label":"Exento","components":[]}""";
    private const string Telecom = """{"label":"Telecomunicaciones","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"},{"tax_code":"ISC","rate":"0.10","effect":"SELECTIVE_TAX"},{"tax_code":"CDT","rate":"0.02","effect":"OTHER_TAX"}]}""";
    private const string Tip = """{"label":"Consumo con propina","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"},{"tax_code":"PROPINA","rate":"0.10","effect":"LEGAL_TIP"}]}""";

    private static async Task<JsonElement> Determine(TestHarness h, string key, Guid party, params TaxLineInput[] lines)
        => JsonDocument.Parse((await h.RunAsync(new TestDetermineTax(h.CompanyId, h.SessionId, key, Day, party, lines), new TestDetermineTaxHandler())).ResultPayload).RootElement;

    private static string Taxes(JsonElement result) => string.Join(",", result.GetProperty("taxes").EnumerateArray().Select(t => t.GetString()));

    private static async Task<Guid> TypeAsync(TestHarness h, FiscalActors actors, string code, string definition)
    {
        await h.ActivateRuleAsync(actors, code.ToLowerInvariant(), code, FiscalRuleKinds.PurchaseTaxType, definition, From);
        return await h.ScalarAsync<Guid>("SELECT rule_id FROM tax.fiscal_rule WHERE company_id = @c AND code = @code", ("c", h.CompanyId), ("code", code));
    }

    private static TaxLineInput Expense(Guid taxType, decimal net, string scope = TaxLineScopes.ExpenseService) => new(Guid.CreateVersion7(), null, net, taxType, scope);

    [Fact]
    public async Task Each_expense_line_carries_only_the_components_of_its_own_tax_type()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var (t18, t16, exempt, telecom, tip) = (
            await TypeAsync(h, actors, "ITBIS_18", Itbis18), await TypeAsync(h, actors, "ITBIS_16", Itbis16), await TypeAsync(h, actors, "EXENTO", Exempt),
            await TypeAsync(h, actors, "TELECOM", Telecom), await TypeAsync(h, actors, "CONSUMO_PROPINA", Tip));
        var supplier = await h.CreateActiveSupplierAsync("101000011", "Servicios del Este, S.R.L.");

        // No PURCHASE_ITBIS rule exists: a document of expense lines does not need it (E-GAS-02-4).
        var mixed = await Determine(h, "d-1", supplier, Expense(t18, 10000m), Expense(t16, 1000m), Expense(exempt, 5000m));
        var phone = await Determine(h, "d-2", supplier, Expense(telecom, 5000m));
        var meal = await Determine(h, "d-3", supplier, Expense(tip, 2000m, TaxLineScopes.ExpenseGoods));
        var types = JsonDocument.Parse(await h.QueryAsync(new ListPurchaseTaxTypes(h.CompanyId, await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR"), Day), new ListPurchaseTaxTypesHandler())).RootElement;

        // 10,000 × 18 % and 1,000 × 16 %; nothing for the exempt line. 5,000: ITBIS 900, ISC 500, CDT 100. 2,000: ITBIS 360, tip 200.
        Assert.Equal("ITBIS:1800.00:RECOVERABLE_INPUT,ITBIS:160.00:RECOVERABLE_INPUT", Taxes(mixed));
        Assert.Equal("1960.00", mixed.GetProperty("recoverable").GetString());
        Assert.Equal("ITBIS:900.00:RECOVERABLE_INPUT,ISC:500.00:SELECTIVE_TAX,CDT:100.00:OTHER_TAX", Taxes(phone));
        Assert.Equal("900.00", phone.GetProperty("recoverable").GetString());
        Assert.Equal("ITBIS:360.00:RECOVERABLE_INPUT,PROPINA:200.00:LEGAL_TIP", Taxes(meal));
        Assert.Equal(
            "CONSUMO_PROPINA:Consumo con propina:2,EXENTO:Exento:0,ITBIS_16:ITBIS 16 %:1,ITBIS_18:ITBIS 18 %:1,TELECOM:Telecomunicaciones:3",
            string.Join(',', types.GetProperty("items").EnumerateArray().Select(t => $"{t.GetProperty("code").GetString()}:{t.GetProperty("label").GetString()}:{t.GetProperty("components").GetArrayLength()}")));
        Assert.Equal(
            "TELECOM|EXPENSE_SERVICE|5000",
            await h.ScalarAsync<string>(
                "SELECT (inputs -> 'lines' -> 0 ->> 'taxType') || '|' || (inputs -> 'lines' -> 0 ->> 'scope') || '|' || (inputs -> 'lines' -> 0 ->> 'netAmount') FROM tax.tax_determination WHERE determination_id = @d",
                ("d", phone.GetProperty("determinationId").GetGuid())));
    }

    [Fact]
    public async Task Inventory_lines_keep_the_automatic_ITBIS_and_withholdings_follow_their_scope()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis", "ITBIS_COMPRAS", FiscalRuleKinds.PurchaseItbis, """{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"}""", From);
        var telecom = await TypeAsync(h, actors, "TELECOM", Telecom);
        await h.ActivateRuleAsync(actors, "isr", "RET_ISR_SERVICIOS", FiscalRuleKinds.PurchaseWithholding,
            """{"tax_code":"RET_ISR","rate":"0.10","base":"NET","party_types":["INDIVIDUAL"],"isr_withholding_type":"2","applies_to":["EXPENSE_SERVICE"]}""", From);
        await h.ActivateRuleAsync(actors, "ritbis", "RET_ITBIS_PF", FiscalRuleKinds.PurchaseWithholding,
            """{"tax_code":"RET_ITBIS","rate":"0.30","base":"ITBIS","party_types":["INDIVIDUAL"]}""", From);
        var individual = await h.CreateActiveSupplierAsync("00112345678", "Juan Pérez");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");

        var raw = await Determine(h, "d-1", individual, new TaxLineInput(Guid.CreateVersion7(), cement, 1000m));
        var service = await Determine(h, "d-2", individual, Expense(telecom, 5000m));
        var goods = await Determine(h, "d-3", individual, Expense(telecom, 5000m, TaxLineScopes.ExpenseGoods));

        // The ISR withholding is for services only; the one on ITBIS is on the line's ITBIS (900.00), never on its ISC or CDT.
        Assert.Equal("ITBIS:180.00:RECOVERABLE_INPUT,RET_ITBIS:54.00:WITHHOLDING", Taxes(raw));
        Assert.Equal("ITBIS:900.00:RECOVERABLE_INPUT,ISC:500.00:SELECTIVE_TAX,CDT:100.00:OTHER_TAX,RET_ISR:500.00:WITHHOLDING,RET_ITBIS:270.00:WITHHOLDING", Taxes(service));
        Assert.Equal("ITBIS:900.00:RECOVERABLE_INPUT,ISC:500.00:SELECTIVE_TAX,CDT:100.00:OTHER_TAX,RET_ITBIS:270.00:WITHHOLDING", Taxes(goods));
    }

    [Fact]
    public async Task Only_the_tax_types_a_document_names_can_close_its_gate()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var t18 = await TypeAsync(h, actors, "ITBIS_18", Itbis18);
        await h.ConfigureAsync(actors, "pending", "SEGUROS", FiscalRuleKinds.PurchaseTaxType, """{"label":"Seguros","components":[{"tax_code":"ISC","rate":"0.16","effect":"SELECTIVE_TAX"}]}""", From);
        var pending = await h.ScalarAsync<Guid>("SELECT rule_id FROM tax.fiscal_rule WHERE code = 'SEGUROS'");
        var supplier = await h.CreateActiveSupplierAsync("101000011", "Servicios del Este, S.R.L.");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");

        // A type still waiting for its source stops nothing that does not use it.
        var ok = await Determine(h, "d-1", supplier, Expense(t18, 100m));
        var notActive = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-2", supplier, Expense(pending, 100m)));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-3", supplier, Expense(Guid.NewGuid(), 100m)));
        var noScope = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-4", supplier, new TaxLineInput(Guid.CreateVersion7(), null, 100m, t18)));
        var both = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-5", supplier, new TaxLineInput(Guid.CreateVersion7(), cement, 100m, t18, TaxLineScopes.ExpenseGoods)));
        var noItbisRule = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-6", supplier, new TaxLineInput(Guid.CreateVersion7(), cement, 100m)));

        Assert.Equal("ITBIS:18.00:RECOVERABLE_INPUT", Taxes(ok));
        Assert.Equal(
            (TaxErrors.FiscalGateClosed, TaxErrors.SubjectInvalid, TaxErrors.SubjectInvalid, TaxErrors.SubjectInvalid, TaxErrors.FiscalGateClosed),
            (notActive.Code, unknown.Code, noScope.Code, both.Code, noItbisRule.Code));
        Assert.Contains("SEGUROS", notActive.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"components":[]}""")]                                                                                      // no label
    [InlineData("""{"label":"X"}""")]                                                                                          // no components
    [InlineData("""{"label":"X","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"WITHHOLDING"}]}""")]                 // not a component effect
    [InlineData("""{"label":"X","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"NON_RECOVERABLE_INPUT"}]}""")]       // out of scope (ITBIS to cost)
    [InlineData("""{"label":"X","components":[{"tax_code":"ITBIS","rate":"18","effect":"RECOVERABLE_INPUT"}]}""")]             // a rate over 1
    [InlineData("""{"label":"X","components":[{"tax_code":"ISC","rate":"0.10","effect":"SELECTIVE_TAX"},{"tax_code":"ISC","rate":"0.02","effect":"OTHER_TAX"}]}""")] // repeated code
    [InlineData("""{"label":"X","components":[{"tax_code":"ISC","rate":"0.10","effect":"SELECTIVE_TAX","base":"NET"}]}""")]    // unknown key
    [InlineData("""{"label":"X","components":[],"exempt_item_categories":[]}""")]                                             // a key of another kind
    public void A_tax_type_definition_is_refused_when_it_is_not_well_formed(string definition)
        => Assert.Equal(TaxErrors.FiscalRuleInvalid, Assert.Throws<DomainException>(() => FiscalRuleDefinition.Parse(FiscalRuleKinds.PurchaseTaxType, definition)).Code);

    [Fact]
    public void A_withholding_rule_names_the_lines_it_applies_to_or_all_of_them()
    {
        var all = FiscalRuleDefinition.Parse(FiscalRuleKinds.PurchaseWithholding, TaxSetup.WithholdingDefinition);
        var services = FiscalRuleDefinition.Parse(FiscalRuleKinds.PurchaseWithholding, """{"tax_code":"RET_ISR","rate":"0.10","base":"NET","party_types":["INDIVIDUAL"],"applies_to":["EXPENSE_SERVICE"]}""");
        var empty = Assert.Throws<DomainException>(() => FiscalRuleDefinition.Parse(
            FiscalRuleKinds.PurchaseWithholding, """{"tax_code":"RET_ISR","rate":"0.10","base":"NET","party_types":["INDIVIDUAL"],"applies_to":[]}"""));
        var unknown = Assert.Throws<DomainException>(() => FiscalRuleDefinition.Parse(
            FiscalRuleKinds.PurchaseWithholding, """{"tax_code":"RET_ISR","rate":"0.10","base":"NET","party_types":["INDIVIDUAL"],"applies_to":["SERVICES"]}"""));

        Assert.Null(all.AppliesTo);
        Assert.Equal(["EXPENSE_SERVICE"], services.AppliesTo!);
        Assert.Equal((TaxErrors.FiscalRuleInvalid, TaxErrors.FiscalRuleInvalid), (empty.Code, unknown.Code));
    }

    [Fact]
    public async Task A_tax_type_is_ready_only_with_cases_that_give_every_component()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var version = await h.ConfigureAsync(actors, "cfg", "TELECOM", FiscalRuleKinds.PurchaseTaxType, Telecom, From);
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, "lnk", version, await h.RegisterTestSourceAsync(actors, "src")), new LinkFiscalSourceHandler());

        var wrong = JsonDocument.Parse((await h.RunAsync(
            new RunFiscalRuleTests(h.CompanyId, actors.Analyst, "t1", version, [new FiscalTestCase("solo-itbis", "COMPANY", string.Empty, 5000m, 0m, [new ExpectedTax("ITBIS", 900m, TaxEffects.RecoverableInput)])]),
            new RunFiscalRuleTestsHandler())).ResultPayload).RootElement;
        var right = JsonDocument.Parse((await h.RunAsync(
            new RunFiscalRuleTests(
                h.CompanyId, actors.Analyst, "t2", version,
                [new FiscalTestCase("telefono", "COMPANY", string.Empty, 5000m, 0m,
                    [new ExpectedTax("ITBIS", 900m, TaxEffects.RecoverableInput), new ExpectedTax("ISC", 500m, TaxEffects.SelectiveTax), new ExpectedTax("CDT", 100m, TaxEffects.OtherTax)])]),
            new RunFiscalRuleTestsHandler())).ResultPayload).RootElement;

        Assert.Equal("False|BLOCKED_PENDING_SOURCE", $"{wrong.GetProperty("passed").GetBoolean()}|{wrong.GetProperty("status").GetString()}");
        Assert.Equal("True|READY", $"{right.GetProperty("passed").GetBoolean()}|{right.GetProperty("status").GetString()}");
    }

    [Fact]
    public async Task The_pack_of_tax_types_is_loaded_ready_with_its_sources_and_the_types_coexist_once_activated()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.GrantAsync(h.CompanyId, IdentityConstants.ConfigurationLoadUserId, "CARGA_CONFIGURACION");
        var session = await h.Sessions.StartServiceSessionAsync(IdentityConstants.ConfigurationLoadUserId);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root!.FullName, "Rochell.slnx")))
        {
            root = root.Parent;
        }

        var pack = FiscalRulePack.Parse(File.ReadAllText(Path.Combine(root.FullName, "deploy", "fiscal", "tax-types-2026-10.json")));

        // The official documents are the owner's to download (E-CFG-4); here each is a stand-in with its own bytes.
        var steps = await new FiscalRulePackLoader(h.Pipeline, h.App).LoadAsync(
            h.CompanyId, session, pack, file => System.Text.Encoding.UTF8.GetBytes("documento " + file), FiscalSourceEnvironments.Test, h.Clock.UtcNow.AddSeconds(-1));
        foreach (var id in await h.ScalarAsync<Guid[]>("SELECT array_agg(v.rule_version_id) FROM tax.fiscal_rule_version v WHERE v.status = 'READY'") ?? [])
        {
            await h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, "act-" + id, id), new ActivateFiscalRuleVersionHandler());
        }

        Assert.Equal(
            "CT-TITULO-III:REGISTERED,CT-TITULO-IV:REGISTERED,LEY-153-98:REGISTERED,LEY-16-92:REGISTERED,ITBIS_18:READY,ITBIS_16:READY,EXENTO:READY,TELECOM:READY,SEGUROS:READY,CONSUMO_PROPINA:READY",
            string.Join(',', steps.Select(s => $"{s.Subject}:{s.Outcome}")));
        Assert.Equal(
            "CONSUMO_PROPINA:ACTIVE,EXENTO:ACTIVE,ITBIS_16:ACTIVE,ITBIS_18:ACTIVE,SEGUROS:ACTIVE,TELECOM:ACTIVE",
            await h.ScalarAsync<string>("SELECT string_agg(r.code || ':' || v.status, ',' ORDER BY r.code) FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id"));
    }
}
