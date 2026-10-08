using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Rochell.Api.Deliveries;
using Rochell.Identity;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// ENT1-02 (E-ENT-1…6): the drivers' page over HTTP, without signing in. Off without the link key; with it, a link Core did not sign
/// is INVALID, a PIN for it records nothing, and a confirmation without its key or photo is refused. The full flow runs in the Sales
/// tests (DriverConfirmationTests) and the browser journey.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DriverPagesApiTests(PostgresFixture postgres)
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task The_page_is_off_without_the_link_key()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        using var browser = api.Browser();

        var response = await browser.GetAsync($"{DriverPages.Prefix}/{h.CompanyId}/{Guid.CreateVersion7()}?g=1&k=x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_link_core_did_not_sign_is_invalid_and_a_confirmation_needs_its_key_and_photo()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.GrantAsync(h.CompanyId, IdentityConstants.DeliveryConfirmationUserId, "CONFIRMACION_ENTREGA");
        var evidence = Directory.CreateTempSubdirectory("rochell-evidence-");
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:Deliveries:LinkKey"] = Key, ["Rochell:Deliveries:EvidenceRoot"] = evidence.FullName });
        using var browser = api.Browser();
        var delivery = Guid.CreateVersion7();

        var view = JsonDocument.Parse(await browser.GetStringAsync($"{DriverPages.Prefix}/{h.CompanyId}/{delivery}?g=1&k=forged")).RootElement;
        var pin = await PostAsync(browser, $"{DriverPages.Prefix}/{h.CompanyId}/{delivery}/pin", JsonContent.Create(new { generation = 1, mac = "forged", pin = "1234" }), null);
        var noKey = await PostAsync(browser, $"{DriverPages.Prefix}/{h.CompanyId}/{delivery}/confirm", new MultipartFormDataContent { { new StringContent("1"), "generation" } }, null);
        var noPhoto = await PostAsync(browser, $"{DriverPages.Prefix}/{h.CompanyId}/{delivery}/confirm", new MultipartFormDataContent { { new StringContent("1"), "generation" } }, "driver-page-test-1");
        var otherCompany = await browser.GetAsync($"{DriverPages.Prefix}/{Guid.CreateVersion7()}/{delivery}?g=1&k=forged");

        Assert.Equal("INVALID", view.GetProperty("State").GetString());
        Assert.Equal("INVALID", JsonDocument.Parse(await pin.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM log.delivery_link_attempt"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "DRIVER_CONFIRMATION_INVALID"), await noKey.ProblemAsync());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "DRIVER_CONFIRMATION_INVALID"), await noPhoto.ProblemAsync());
        Assert.Equal(HttpStatusCode.NotFound, otherCompany.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, HttpContent content, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("X-Rochell-Csrf", "1");
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }
}
