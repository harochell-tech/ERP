namespace Rochell.TestInfrastructure;

public static class CorrectionSetup
{
    public const string R03A = "0192f001-0000-7000-8000-000000000003";
    public const string R03B = "0192f001-0000-7000-8000-000000000004";

    /// <summary>INVENTORY policy (materiality 10 000), PPV and MUV maps and, optionally, R-03A/R-03B approved.</summary>
    public static async Task EnableCorrectionsAsync(this TestHarness h, bool approveRules = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        await h.CreateActivePolicyAsync("INVENTORY", PolicySetup.Inventory);
        await h.EnableReallocationAsync(approveR02B: false);
        await h.CreateActiveMapAsync("MATERIAL_USAGE_VARIANCE", await h.CreateAccountAsync("5110", "Variación de uso de material", isControl: false));
        if (approveRules)
        {
            await h.AdminRequireAsync(
                $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{R03A}', '{R03B}') AND version = 1");
        }
    }
}
