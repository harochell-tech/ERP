using System.Text.Json;
using Rochell.Finance.Configuration;
using Rochell.Sales.Customers;
using Rochell.Sales.Zones;
using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>
/// PRS-05 (E-PRS-05-7): what the price list journey needs — the zones Higüey and Bávaro, the freight item ACTIVE, FREIGHT_REVENUE
/// mapped to 40500 and P-16 version 2 approved (A-01), and the customer «Hotel Playa Bávaro» on GENERAL with approved terms. GENERAL
/// has no freight, so the other journeys' orders keep their totals. A development world, never a migration.
/// </summary>
internal static class PriceSeed
{
    public static async Task RunAsync(TestHarness h, Guid controller)
    {
        var credit = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'CREDITO' LIMIT 1");
        var seller = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'VENDEDOR' LIMIT 1");
        var creditSession = await h.CreateSessionAsync(credit);
        foreach (var zone in new[] { "Higüey", "Bávaro" })
        {
            await h.RunAsync(new CreateDeliveryZone(h.CompanyId, creditSession, "dev-zone-" + zone, zone), new CreateDeliveryZoneHandler());
        }

        await h.AdminRequireAsync(
            $"INSERT INTO md.item VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', 'TRANSPORTE', 'Transporte de blocks', 'SERVICE', 'un', 'TRANSPORTE', 'ACTIVE', 1)");
        await h.CreateActiveMapAsync("FREIGHT_REVENUE", await h.CreateAccountAsync("40500", "Ingresos por transporte", isControl: false));
        await h.RunAsync(new ApprovePostingRuleVersion(h.CompanyId, controller, "dev-p16-v2", "P-16", 2), new ApprovePostingRuleVersionHandler());

        var sellerSession = await h.CreateSessionAsync(seller);
        var hotel = (await h.RunAsync(new CreateCustomer(h.CompanyId, sellerSession, "dev-hotel", "101000001", "Hotel Playa Bávaro"), new CreateCustomerHandler())).ResultRef;
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, creditSession, "dev-hotel-terms", hotel, 30, 1000000.00m, false), new PrepareCustomerTermsHandler()))
            .ResultPayload).RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, controller, "dev-hotel-terms-a", terms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, creditSession, "dev-hotel-a", hotel, 1), new ActivateCustomerHandler());
    }
}
