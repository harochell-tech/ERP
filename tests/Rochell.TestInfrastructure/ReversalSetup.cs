namespace Rochell.TestInfrastructure;

public static class ReversalSetup
{
    public const string R02B = "0192f001-0000-7000-8000-000000000002";

    /// <summary>Maps PURCHASE_PRICE_VARIANCE and (optionally) approves R-02B.</summary>
    public static async Task<Guid> EnableReallocationAsync(this TestHarness h, bool approveR02B = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        var ppv = await h.CreateAccountAsync("5105", "Variación de precio de compra", isControl: false);
        await h.CreateActiveMapAsync("PURCHASE_PRICE_VARIANCE", ppv);
        if (approveR02B)
        {
            await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R02B}' AND version = 1");
        }

        return ppv;
    }

    /// <summary>Enables the R-T1 test issue fixture (TEST.ISSUE, TEST_EXPENSE) so tests can consume stock.</summary>
    public static async Task EnableTestIssueAsync(this TestHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        await h.CreateActiveMapAsync("TEST_EXPENSE", await h.CreateAccountAsync("6100", "Consumo de prueba", isControl: false));
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000f2' AND version = 1");
    }
}
