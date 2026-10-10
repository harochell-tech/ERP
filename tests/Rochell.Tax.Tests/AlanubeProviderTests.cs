using System.Net;
using System.Text.Json.Nodes;
using Rochell.Tax.Ecf;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>E-VS4-06-1: in Sandbox the sender RNC goes out as Alanube's test company; the stored payload and Production keep the company's own.</summary>
public sealed class AlanubeProviderTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"01DOC","status":"REGISTERED"}""") };
        }
    }

    private static (AlanubeProvider Provider, Capture Capture, HttpClient Http) Provider(string mode, string? sandboxRnc)
    {
        var capture = new Capture();
        var http = new HttpClient(capture);
        var settings = new EcfSettings { Mode = mode, BaseUrl = "https://sandbox.alanube.co/dom/v1/", Token = "t", SandboxSenderRnc = sandboxRnc };
        return (new AlanubeProvider(http, settings), capture, http);
    }

    private static JsonObject Invoice() => new() { ["sender"] = new JsonObject { ["rnc"] = "131925332", ["companyName"] = "BLOCK ROCHELL SRL" } };

    private static JsonObject Annulment() => new() { ["header"] = new JsonObject { ["rncSender"] = 131925332L, ["cancelledEncfQuantity"] = 1 } };

    [Fact]
    public async Task In_sandbox_the_test_company_rnc_goes_out_and_the_stored_payload_keeps_the_company_rnc()
    {
        var (provider, capture, http) = Provider(EcfModes.Sandbox, "132109122");
        using var _ = http;
        var invoice = Invoice();
        var annulment = Annulment();

        await provider.SubmitAsync("31", invoice, CancellationToken.None);
        await provider.CancelAsync(annulment, CancellationToken.None);

        Assert.Equal("\"132109122\"", capture.Bodies[0]["sender"]!["rnc"]!.ToJsonString());
        Assert.Equal("BLOCK ROCHELL SRL", capture.Bodies[0]["sender"]!["companyName"]!.GetValue<string>());
        Assert.Equal("132109122", capture.Bodies[1]["header"]!["rncSender"]!.ToJsonString());
        Assert.Equal("131925332", invoice["sender"]!["rnc"]!.GetValue<string>());
        Assert.Equal(131925332L, annulment["header"]!["rncSender"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(EcfModes.Production, "132109122")]
    [InlineData(EcfModes.Sandbox, null)]
    public async Task Otherwise_the_company_rnc_goes_out(string mode, string? sandboxRnc)
    {
        var (provider, capture, http) = Provider(mode, sandboxRnc);
        using var _ = http;

        await provider.SubmitAsync("31", Invoice(), CancellationToken.None);
        await provider.CancelAsync(Annulment(), CancellationToken.None);

        Assert.Equal("131925332", capture.Bodies[0]["sender"]!["rnc"]!.GetValue<string>());
        Assert.Equal("131925332", capture.Bodies[1]["header"]!["rncSender"]!.ToJsonString());
    }
}
