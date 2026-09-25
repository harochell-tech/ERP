using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Tax;

namespace Rochell.TestInfrastructure;

/// <summary>Test-only command that runs the Tax Engine inside the command pipeline.</summary>
public sealed record TestDetermineTax(Guid CompanyId, Guid SessionId, string IdempotencyKey, DateOnly Date, Guid PartyId, IReadOnlyList<TaxLineInput> Lines) : ICommand;

[RequiresPermission("test:ping")]
public sealed class TestDetermineTaxHandler : ICommandHandler<TestDetermineTax>
{
    public string CommandType => "Test.DetermineTax";

    public async Task<string> HandleAsync(TestDetermineTax command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var determination = await new TaxEngine().DetermineAsync(
            context, new TaxRequest("TestPurchase", context.ResultRef, command.Date, command.PartyId, command.Lines), cancellationToken);
        return JsonSerializer.Serialize(new
        {
            determinationId = determination.DeterminationId,
            recoverable = determination.RecoverableInput.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), // ADR-015: decimals as strings
            withholding = determination.Withholding.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            nonRecoverable = determination.HasNonRecoverableInput,
            taxes = determination.Taxes.Select(t => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t.TaxCode}:{t.Amount:0.00}:{t.Effect}")),
        });
    }
}

/// <summary>The two fiscal actors (SoD: configure ⟂ activate) and a helper that takes a rule version through the whole gate with TEST sources.</summary>
public sealed record FiscalActors(Guid Analyst, Guid Specialist);

public static class TaxSetup
{
    public const string ItbisDefinition = """{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT","exempt_item_categories":["AGREGADO"]}""";
    public const string WithholdingDefinition = """{"tax_code":"RET_ITBIS","rate":"0.30","base":"ITBIS","party_types":["INDIVIDUAL"]}""";

    /// <summary>Initializes the deployment environment as TEST (what `rochell-migrate init-environment TEST` does), unless set.</summary>
    public static Task InitTestEnvironmentAsync(this TestHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        return h.AdminRequireAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('TEST', current_user, now()) ON CONFLICT DO NOTHING");
    }

    public static async Task<FiscalActors> FiscalActorsAsync(this TestHarness h, bool initEnvironment = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        if (initEnvironment)
        {
            await h.InitTestEnvironmentAsync();
        }

        return new FiscalActors(await h.SessionWithRolesAsync("ANALISTA_FISCAL"), await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"));
    }

    /// <summary>Registers a source marked TEST (P-7), or PRODUCTION when <paramref name="environment"/> says so.</summary>
    public static async Task<Guid> RegisterTestSourceAsync(this TestHarness h, FiscalActors actors, string key, DateOnly? effectiveFrom = null, string environment = FiscalSourceEnvironments.Test)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(actors);
        var result = await h.RunAsync(
            new RegisterFiscalSource(
                h.CompanyId, actors.Analyst, key, "TEST — fuente de prueba", "Norma de prueba", "v1", new DateOnly(2025, 12, 1), h.Clock.UtcNow.AddDays(-1),
                effectiveFrom ?? new DateOnly(2025, 12, 1), null, "https://example.test/norma", "test/norma-v1.pdf", new string('a', 64), environment),
            new RegisterFiscalSourceHandler());
        return result.ResultRef;
    }

    public static async Task<Guid> ConfigureAsync(this TestHarness h, FiscalActors actors, string key, string code, string kind, string definition, DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(actors);
        return (await h.RunAsync(new ConfigureFiscalRuleVersion(h.CompanyId, actors.Analyst, key, code, kind, definition, from), new ConfigureFiscalRuleVersionHandler())).ResultRef;
    }

    /// <summary>A case the version passes: taxes computed by the pure calculator for the given context.</summary>
    public static FiscalTestCase PassingCase(string kind, string definition, string id = "caso-1")
    {
        var rule = new ApplicableRule(Guid.Empty, "X", FiscalRuleDefinition.Parse(kind, definition));
        var line = new TaxableLine(Guid.Empty, "CEMENTO", 1000m);
        var tax = kind == FiscalRuleKinds.PurchaseItbis ? TaxCalculator.Itbis(rule, line) : TaxCalculator.Withholding(rule, line, PartyTaxTypes.Individual, 180m);
        return new FiscalTestCase(id, PartyTaxTypes.Individual, "CEMENTO", 1000m, 180m, tax is null ? [] : [new ExpectedTax(tax.TaxCode, tax.Amount, tax.Effect)]);
    }

    /// <summary>Configure → TEST source → link → passing tests → activate. Returns the version id.</summary>
    public static async Task<Guid> ActivateRuleAsync(this TestHarness h, FiscalActors actors, string key, string code, string kind, string definition, DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(actors);
        var version = await h.ConfigureAsync(actors, key + "-cfg", code, kind, definition, from);
        var source = await h.RegisterTestSourceAsync(actors, key + "-src");
        await h.RunAsync(new LinkFiscalSource(h.CompanyId, actors.Analyst, key + "-lnk", version, source), new LinkFiscalSourceHandler());
        await h.RunAsync(new RunFiscalRuleTests(h.CompanyId, actors.Analyst, key + "-tst", version, [PassingCase(kind, definition)]), new RunFiscalRuleTestsHandler());
        await h.RunAsync(new ActivateFiscalRuleVersion(h.CompanyId, actors.Specialist, key + "-act", version), new ActivateFiscalRuleVersionHandler());
        return version;
    }
}
