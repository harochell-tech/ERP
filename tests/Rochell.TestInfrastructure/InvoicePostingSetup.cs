using Rochell.Tax;

namespace Rochell.TestInfrastructure;

public static class InvoicePostingSetup
{
    public const string R04 = "0192f001-0000-7000-8000-000000000005";
    public const string R05 = "0192f001-0000-7000-8000-000000000006";
    public const string R07B = "0192f001-0000-7000-8000-000000000007";

    /// <summary>18 % recoverable ITBIS with no exemption (the setup's item is an aggregate).</summary>
    public const string FullItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"}""";

    /// <summary>
    /// Everything posting needs: fiscal rules through the gate (TEST sources), ITBIS / AP / withholding / PPV accounts and maps,
    /// and R-04, R-05, R-07B approved (unless <paramref name="approveRules"/> is false).
    /// </summary>
    public static async Task EnableInvoicePostingAsync(this TestHarness h, string itbisDefinition = FullItbis, string? withholdingDefinition = null, bool approveRules = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis", "ITBIS-COMPRAS", FiscalRuleKinds.PurchaseItbis, itbisDefinition, new DateOnly(2026, 1, 1));
        if (withholdingDefinition is not null)
        {
            await h.ActivateRuleAsync(actors, "ret", "RET-COMPRAS", FiscalRuleKinds.PurchaseWithholding, withholdingDefinition, new DateOnly(2026, 1, 1));
        }

        await h.CreateActiveMapAsync("ITBIS_RECOVERABLE", await h.CreateAccountAsync("1405", "ITBIS pagado en compras", isControl: false));
        await h.CreateActiveMapAsync("AP_CONTROL", await h.CreateAccountAsync("2101", "Cuentas por pagar proveedores", isControl: true));
        await h.CreateActiveMapAsync("WITHHOLDING_PAYABLE", await h.CreateAccountAsync("2110", "Retenciones por pagar", isControl: false));
        await h.EnableReallocationAsync(approveR02B: false);
        if (approveRules)
        {
            await h.AdminRequireAsync(
                $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{R04}', '{R05}', '{R07B}') AND version = 1");
        }
    }
}
