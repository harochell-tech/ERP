using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>UX4-01: each reconciliation's latest run with its cutoff and the two sides in words (E-UX4-2); the Contador reads them (E-UX4-13).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4ReconciliationTests(PostgresFixture postgres)
{
    /// <summary>The reconciliations that store totals: the definitions with a totals query and BANK-GL.</summary>
    private static readonly string[] WithTotals =
        ["AP-GL", "AR-GL", "BANK-GL", "CONTRACT-ASSET", "INV-QTY-BALANCE", "INV-VALUE-BALANCE", "INV-VALUE-GL", "PAY-APPL", "RECEIPT-APPL", "TB-BALANCED", "VALUE-GL-LINK", "WIP-GL"];

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    [Fact]
    public async Task The_latest_run_of_each_reconciliation_shows_its_cutoff_and_what_each_side_is()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var controller = s.Purchasing.Controller;
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var cutoff = Today(h).AddDays(-1);
        await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "first", ["INV-VALUE-GL", "GRNI-AGING"], cutoff), new RunReconciliationHandler());
        await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "second", ["INV-VALUE-GL"]), new RunReconciliationHandler());

        var latest = JsonDocument.Parse(await h.QueryAsync(new ListLatestReconciliationRuns(h.CompanyId, contador), new ListLatestReconciliationRunsHandler())).RootElement.GetProperty("items");
        var runs = JsonDocument.Parse(await h.QueryAsync(new ListReconciliationRuns(h.CompanyId, contador, "GRNI-AGING"), new ListReconciliationRunsHandler())).RootElement.GetProperty("items");
        var definitions = JsonDocument.Parse(await h.QueryAsync(new ListReconciliationDefinitions(h.CompanyId, contador), new ListReconciliationDefinitionsHandler())).RootElement.GetProperty("items");
        var periods = JsonDocument.Parse(await h.QueryAsync(new ListPeriods(h.CompanyId, contador, Today(h).Year), new ListPeriodsHandler())).RootElement.GetProperty("items");
        JsonElement Latest(string code) => latest.EnumerateArray().Single(l => l.GetProperty("reconCode").GetString() == code);

        // INV-VALUE-GL: the run without cutoff is the latest; it carries its totals with their labels. GRNI-AGING has no totals, AP-GL never ran.
        var value = Latest("INV-VALUE-GL").GetProperty("latestRun");
        Assert.Equal((JsonValueKind.Null, "Valor del inventario", "Saldo contable del inventario", 9000m), (value.GetProperty("cutoffDate").ValueKind,
            value.GetProperty("sideALabel").GetString(), value.GetProperty("sideBLabel").GetString(),
            decimal.Parse(value.GetProperty("totalA").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal((cutoff.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), JsonValueKind.Null),
            (Latest("GRNI-AGING").GetProperty("latestRun").GetProperty("cutoffDate").GetString(), Latest("GRNI-AGING").GetProperty("sideALabel").ValueKind));
        Assert.Equal(JsonValueKind.Null, Latest("AP-GL").GetProperty("latestRun").ValueKind);
        Assert.Equal(("Saldo abierto de cuentas por pagar", "Saldo de la cuenta de control de proveedores"), (Latest("AP-GL").GetProperty("sideALabel").GetString(), Latest("AP-GL").GetProperty("sideBLabel").GetString()));
        Assert.Equal(31, latest.GetArrayLength()); // + PROFORMA-ASIG (E-FIS1b-01-12)
        Assert.Equal(cutoff.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Assert.Single(runs.EnumerateArray()).GetProperty("cutoffDate").GetString());
        Assert.Equal(WithTotals, definitions.EnumerateArray().Where(d => d.GetProperty("sideALabel").ValueKind == JsonValueKind.String).Select(d => d.GetProperty("reconCode").GetString()).ToArray());
        Assert.Equal(12, periods.GetArrayLength());
    }

    [Fact]
    public async Task Every_run_that_stores_totals_has_both_labels()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.RunAsync(new RunReconciliation(h.CompanyId, s.Purchasing.Controller, "all"), new RunReconciliationHandler());

        Assert.Equal(0L, await h.ScalarAsync<long>(
            """
            SELECT count(*) FROM rec.recon_run r JOIN rec.recon_definition d ON d.recon_code = r.recon_code
            WHERE r.total_a IS NOT NULL AND (d.side_a_label IS NULL OR d.side_b_label IS NULL)
            """));
        Assert.True(await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_run WHERE total_a IS NOT NULL") >= 10);
    }
}
