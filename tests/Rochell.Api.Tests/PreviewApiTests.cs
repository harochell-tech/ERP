using System.Net;
using System.Text;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>UX4-01 · E-UX4-3: a preview is a query sent with POST (its lines travel in the body) — read-only, no idempotency key.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PreviewApiTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_purchase_order_preview_reads_its_body_writes_nothing_and_refuses_bad_input()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var p = await h.CreatePurchasingSetupAsync();
        var buyer = await api.SignInAsSessionUserAsync(p.Buyer);
        var today = BusinessCalendar.DefaultBusinessDate(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var path = $"/api/v1/companies/{h.CompanyId}/procurement/purchase-orders/preview";
        Task<HttpResponseMessage> Post(string json, HttpClient? client = null)
            => (client ?? buyer).PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = $$"""{"plantId":"{{p.PlantId}}","partyId":"{{p.SupplierId}}","orderDate":"{{today}}","lines":[{"itemId":"{{p.Sand}}","uom":"t","quantity":"2","unitPrice":"1500.505"}]}""";
        var commands = await h.CountAsync("core.command_log");

        var ok = await Post(body);
        var okText = await ok.Content.ReadAsStringAsync();
        var number = await Post(body.Replace("\"quantity\":\"2\"", "\"quantity\":2", StringComparison.Ordinal));
        var serverField = await Post(body.Replace("{\"plantId\"", $"{{\"companyId\":\"{h.CompanyId}\",\"plantId\"", StringComparison.Ordinal));
        var anonymous = await Post(body, api.Browser());
        var credit = await buyer.GetAsync($"/api/v1/companies/{h.CompanyId}/sales/customers/{p.SupplierId}/credit-preview?amount=diez");

        // 2 × 1 500.505 = 3 001.01; no purchase ITBIS rule in force: the estimate is null with FISCAL_GATE_CLOSED.
        Assert.True(ok.StatusCode == HttpStatusCode.OK, okText);
        Assert.Contains("\"netTotal\":\"3001.01\"", okText, StringComparison.Ordinal);
        Assert.Contains("\"itbisUnavailableCode\":\"FISCAL_GATE_CLOSED\"", okText, StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_REQUEST"), await number.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_REQUEST"), await serverField.ProblemAsync());
        Assert.Equal((HttpStatusCode.Unauthorized, "SESSION_INVALID"), await anonymous.ProblemAsync());
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_PARAMETER"), await credit.ProblemAsync());
        Assert.Equal(commands, await h.CountAsync("core.command_log"));
    }
}
