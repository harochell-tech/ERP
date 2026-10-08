using System.Net;
using System.Text.Json;
using Rochell.Tax.Ecf;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>E-VS4-04-8: the sandbox contract test — its cases in order, one report line per request, never the token, never Production.</summary>
public sealed class EcfContractTestTests
{
    private const string Token = "secret-token-value";

    private sealed class FakeAlanube : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var body = request.Method == HttpMethod.Get ? """{"id":"01DOC","status":"FINISHED","legalStatus":"ACCEPTED"}""" : """{"id":"01DOC","status":"REGISTERED"}""";
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Created) { Content = new StringContent(body) });
        }
    }

    private static EcfContractSettings Settings(string url = "https://sandbox.alanube.co/dom/v1/") => new()
    {
        BaseUrl = url,
        TokenFile = "/dev/null",
        SenderRnc = "131925332",
        SenderName = "BLOCK ROCHELL SRL",
        SenderAddress = "Higüey",
        BuyerRnc = "101010101",
        BuyerName = "CLIENTE DE PRUEBA",
        First31 = 1,
        First34 = 1,
        SequenceDueDate = "2027-12-31",
        UnitPrice = "100.00",
        ItbisRate = "0.18",
        Burst = 3,
        PollSeconds = 0,
    };

    [Fact]
    public async Task It_runs_the_automatable_cases_in_order_and_writes_every_answer_without_the_token()
    {
        var alanube = new FakeAlanube();
        using var http = new HttpClient(alanube) { BaseAddress = new Uri("https://sandbox.alanube.co/dom/v1/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        var report = new StringWriter();

        var requests = await new EcfContractTest(http, Settings(), report, (_, _) => Task.CompletedTask).RunAsync(CancellationToken.None);

        var lines = report.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(lines.Count, requests);
        Assert.Equal(
            "CT-01,CT-03,CT-05,CT-06,CT-07,CT-08,CT-12,CT-13,CT-14",
            string.Join(',', lines.Select(l => l.GetProperty("case").GetString()).Distinct()));
        Assert.DoesNotContain(Token, report.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, lines.Count(l => l.GetProperty("case").GetString() == "CT-14"));
        var first = lines.First(l => l.GetProperty("what").GetString() == "31 with E310000000001").GetProperty("request");
        Assert.Equal(("18.00", "118.00"), (first.GetProperty("totals").GetProperty("itbisTotal").GetRawText(), first.GetProperty("totals").GetProperty("totalAmount").GetRawText()));
        Assert.Contains("POST /dom/v1/credit-notes", alanube.Requests);
        Assert.Contains("GET /dom/v1/fiscal-invoices/01DOC", alanube.Requests);
    }

    [Fact]
    public void It_refuses_anything_but_the_sandbox()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Settings("https://api.alanube.co/dom/v1/").Validate());

        Assert.Contains("sandbox", ex.Message, StringComparison.Ordinal);
    }
}
