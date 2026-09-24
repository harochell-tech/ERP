using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>§30 Tax Engine behind the gate: ITBIS, exemptions, withholding by supplier type (E-PR12-6), rounding (E-PR12-7), versions by date, SI-07 basis.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TaxEngineTests(PostgresFixture postgres)
{
    private static readonly DateOnly From = new(2026, 1, 1);

    private static async Task<JsonElement> Determine(TestHarness h, string key, DateOnly date, Guid party, params TaxLineInput[] lines)
        => JsonDocument.Parse((await h.RunAsync(new TestDetermineTax(h.CompanyId, h.SessionId, key, date, party, lines), new TestDetermineTaxHandler())).ResultPayload).RootElement;

    private static string Taxes(JsonElement result) => string.Join(",", result.GetProperty("taxes").EnumerateArray().Select(t => t.GetString()));

    [Fact]
    public async Task ITBIS_exemptions_and_withholding_follow_the_active_definitions()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var itbis = await h.ActivateRuleAsync(actors, "itbis", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var withholding = await h.ActivateRuleAsync(actors, "ret", "RET-ITBIS-PF", FiscalRuleKinds.PurchaseWithholding, TaxSetup.WithholdingDefinition, From);
        var company = await h.CreateActiveSupplierAsync("101000011", "Cementos del Este, S.R.L.");
        var individual = await h.CreateActiveSupplierAsync("00112345678", "Juan Pérez");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");
        var sand = await h.CreateActiveItemAsync("ARENA", "t", "AGREGADO");
        var (l1, l2) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        var fromCompany = await Determine(h, "d-1", new DateOnly(2026, 3, 1), company, new TaxLineInput(l1, cement, 100000m), new TaxLineInput(l2, sand, 50000m));
        var fromIndividual = await Determine(h, "d-2", new DateOnly(2026, 3, 1), individual, new TaxLineInput(l1, cement, 100000m));

        Assert.Equal("ITBIS:18000.00:RECOVERABLE_INPUT", Taxes(fromCompany));
        Assert.Equal("ITBIS:18000.00:RECOVERABLE_INPUT,RET_ITBIS:5400.00:WITHHOLDING", Taxes(fromIndividual));
        Assert.Equal(5400m, fromIndividual.GetProperty("withholding").GetDecimal());
        Assert.Equal(2L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM tax.tax_determination WHERE rule_version_ids @> ARRAY[@a, @b]::uuid[]", ("a", itbis), ("b", withholding)));
        Assert.Equal("INDIVIDUAL", await h.ScalarAsync<string>(
            "SELECT inputs ->> 'partyTaxType' FROM tax.tax_determination WHERE determination_id = @d", ("d", fromIndividual.GetProperty("determinationId").GetGuid())));
    }

    [Fact]
    public async Task Amounts_round_half_up_to_two_decimals_and_non_recoverable_input_is_flagged()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis,
            """{"tax_code":"ITBIS","rate":"0.18","effect":"NON_RECOVERABLE_INPUT"}""", From);
        var supplier = await h.CreateActiveSupplierAsync("101000011", "Aditivos, S.R.L.");
        var additive = await h.CreateActiveItemAsync("ADITIVO-1", "l", "ADITIVO");

        var result = await Determine(h, "d", new DateOnly(2026, 3, 1), supplier, new TaxLineInput(Guid.CreateVersion7(), additive, 333.33m));

        Assert.Equal("ITBIS:60.00:NON_RECOVERABLE_INPUT", Taxes(result));
        Assert.True(result.GetProperty("nonRecoverable").GetBoolean());
    }

    [Fact]
    public async Task Each_date_uses_the_version_in_force_on_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "v1", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        await h.ActivateRuleAsync(actors, "v2", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis,
            """{"tax_code":"ITBIS","rate":"0.16","effect":"RECOVERABLE_INPUT"}""", new DateOnly(2026, 7, 1));
        var supplier = await h.CreateActiveSupplierAsync("101000011", "Cementos del Este, S.R.L.");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");

        var before = await Determine(h, "d-1", new DateOnly(2026, 6, 30), supplier, new TaxLineInput(Guid.CreateVersion7(), cement, 1000m));
        var after = await Determine(h, "d-2", new DateOnly(2026, 7, 1), supplier, new TaxLineInput(Guid.CreateVersion7(), cement, 1000m));

        Assert.Equal("ITBIS:180.00:RECOVERABLE_INPUT", Taxes(before));
        Assert.Equal("ITBIS:160.00:RECOVERABLE_INPUT", Taxes(after));
    }

    [Fact]
    public async Task The_gate_is_closed_without_ITBIS_or_with_a_rule_pending_its_source()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var supplier = await h.CreateActiveSupplierAsync("00112345678", "Juan Pérez");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");
        var line = new TaxLineInput(Guid.CreateVersion7(), cement, 1000m);

        var noItbis = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-1", new DateOnly(2026, 3, 1), supplier, line));
        await h.ActivateRuleAsync(actors, "itbis", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        await h.ConfigureAsync(actors, "ret", "RET-ITBIS-PF", FiscalRuleKinds.PurchaseWithholding, TaxSetup.WithholdingDefinition, new DateOnly(2026, 2, 1));
        var pending = await Assert.ThrowsAsync<DomainException>(() => Determine(h, "d-2", new DateOnly(2026, 3, 1), supplier, line));
        var beforePending = await Determine(h, "d-3", new DateOnly(2026, 1, 15), supplier, line);

        Assert.Equal(TaxErrors.FiscalGateClosed, noItbis.Code);
        Assert.Equal(TaxErrors.FiscalGateClosed, pending.Code);
        Assert.Contains("RET-ITBIS-PF", pending.Message, StringComparison.Ordinal);
        Assert.Equal("ITBIS:180.00:RECOVERABLE_INPUT", Taxes(beforePending));
        Assert.Equal(1L, await h.CountAsync("tax.tax_determination"));
    }

    [Theory]
    [InlineData("UPDATE tax.tax_determination SET subject_type = 'X'")]
    [InlineData("DELETE FROM tax.tax_determination_line")]
    [InlineData("UPDATE tax.tax_determination_line SET amount = amount")]
    public async Task Determinations_are_immutable_evidence(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, TaxSetup.ItbisDefinition, From);
        var supplier = await h.CreateActiveSupplierAsync("101000011", "Cementos del Este, S.R.L.");
        var cement = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");
        await Determine(h, "d", new DateOnly(2026, 3, 1), supplier, new TaxLineInput(Guid.CreateVersion7(), cement, 1000m));

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
    }
}
