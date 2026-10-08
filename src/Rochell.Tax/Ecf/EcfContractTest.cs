using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochell.Tax.Ecf;

// VS4-04 (E-VS4-04-8, E-VS4-13, N-01): the contract test against Alanube's SANDBOX — the automatable cases CT-01, 03, 05, 06, 07, 08, 12,
// 13 and 14 of v2.1 §5.1, each request and Alanube's raw answer written as one JSON line (the token never). The other cases are answered
// with evidence in docs/acceptance/vs4-contract-test.md. It numbers from a range the DGII gave for the sandbox, never a production one.

/// <summary>The settings file of <c>rochell-migrate ecf-contract-test</c> (no secret in it: the token is read from <see cref="TokenFile"/>).</summary>
public sealed class EcfContractSettings
{
    public string BaseUrl { get; set; } = "https://sandbox.alanube.co/dom/v1/";

    public string TokenFile { get; set; } = string.Empty;

    public string SenderRnc { get; set; } = string.Empty;

    public string SenderName { get; set; } = string.Empty;

    public string SenderAddress { get; set; } = string.Empty;

    public string BuyerRnc { get; set; } = string.Empty;

    public string BuyerName { get; set; } = string.Empty;

    /// <summary>The first unused number of the sandbox's e-CF 31 range (the 10 digits after E31); the test uses 5 + <see cref="Burst"/> of them.</summary>
    public long First31 { get; set; }

    /// <summary>The first unused number of the sandbox's e-CF 34 range.</summary>
    public long First34 { get; set; }

    public string SequenceDueDate { get; set; } = string.Empty;

    /// <summary>The line's price without ITBIS and the sales ITBIS rate, as decimal strings ("100.00", "0.18").</summary>
    public string UnitPrice { get; set; } = string.Empty;

    public string ItbisRate { get; set; } = string.Empty;

    public int Burst { get; set; } = 50;

    public int PollSeconds { get; set; } = 10;

    public int PollTimes { get; set; } = 18;

    public void Validate()
    {
        if (!BaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The contract test runs only against Alanube's sandbox.");
        }

        foreach (var (value, name) in new[]
                 {
                     (TokenFile, nameof(TokenFile)), (SenderRnc, nameof(SenderRnc)), (SenderName, nameof(SenderName)), (SenderAddress, nameof(SenderAddress)),
                     (BuyerRnc, nameof(BuyerRnc)), (BuyerName, nameof(BuyerName)), (SequenceDueDate, nameof(SequenceDueDate)), (UnitPrice, nameof(UnitPrice)),
                     (ItbisRate, nameof(ItbisRate)),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"The settings need {name}.");
            }
        }

        if (First31 < 1 || First34 < 1 || Burst < 0)
        {
            throw new InvalidOperationException("First31 and First34 are the first unused numbers of the sandbox ranges; Burst is 0 or more.");
        }
    }
}

public sealed class EcfContractTest(HttpClient http, EcfContractSettings settings, TextWriter report, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public sealed record Answer(int? Status, string Body, string? Id);

    /// <summary>Runs every automatable case; returns how many requests were made.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var n = settings.First31;
        var requests = 0;

        // CT-01: who assigns the e-NCF — without one, then with one.
        var withoutEncf = Invoice(Encf31(n));
        ((JsonObject)withoutEncf["idDoc"]!).Remove("encf");
        await SendAsync("CT-01", "31 without idDoc.encf", "fiscal-invoices", withoutEncf, cancellationToken).ConfigureAwait(false);
        var a = Invoice(Encf31(n));
        var first = await SendAsync("CT-01", $"31 with {Encf31(n)}", "fiscal-invoices", a, cancellationToken).ConfigureAwait(false);
        requests += 2;

        // CT-03: a validation error — is the number consumed? The invalid document, then a valid one with the same number.
        var invalid = Invoice(Encf31(n + 1));
        invalid.Remove("totals");
        await SendAsync("CT-03", $"31 {Encf31(n + 1)} without totals", "fiscal-invoices", invalid, cancellationToken).ConfigureAwait(false);
        await SendAsync("CT-03", $"31 {Encf31(n + 1)} valid after the invalid one", "fiscal-invoices", Invoice(Encf31(n + 1)), cancellationToken).ConfigureAwait(false);
        requests += 2;

        // CT-05: the client gives up before the answer; sending again tells whether Alanube names the existing document.
        using (var hurry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            hurry.CancelAfter(TimeSpan.FromMilliseconds(1));
            await SendAsync("CT-05", $"31 {Encf31(n + 2)} with a 1 ms client timeout", "fiscal-invoices", Invoice(Encf31(n + 2)), hurry.Token, cancellationToken).ConfigureAwait(false);
        }

        await SendAsync("CT-05", $"31 {Encf31(n + 2)} sent again", "fiscal-invoices", Invoice(Encf31(n + 2)), cancellationToken).ConfigureAwait(false);
        requests += 2;

        // CT-06 / CT-07: the same document twice, then with a minor change.
        await SendAsync("CT-06", $"31 {Encf31(n)} exactly again", "fiscal-invoices", a, cancellationToken).ConfigureAwait(false);
        var changed = Invoice(Encf31(n));
        changed["itemDetails"]![0]!["itemName"] = "PRUEBA CONTRACTUAL CAMBIADA";
        await SendAsync("CT-07", $"31 {Encf31(n)} with a changed item name", "fiscal-invoices", changed, cancellationToken).ConfigureAwait(false);
        requests += 2;

        // CT-08: a document the DGII rejects (the ITBIS total does not match the lines); followed to its answer.
        var wrong = Invoice(Encf31(n + 3));
        wrong["totals"]!["itbis1Total"] = Money(Price + Price);
        wrong["totals"]!["itbisTotal"] = Money(Price + Price);
        wrong["totals"]!["totalAmount"] = Money(Price + Price + Price);
        var rejected = await SendAsync("CT-08", $"31 {Encf31(n + 3)} with a wrong ITBIS", "fiscal-invoices", wrong, cancellationToken).ConfigureAwait(false);
        requests += 1 + await FollowAsync("CT-08", "fiscal-invoices", rejected.Id, cancellationToken).ConfigureAwait(false);

        // CT-12: the status of the first document until it is final.
        requests += await FollowAsync("CT-12", "fiscal-invoices", first.Id, cancellationToken).ConfigureAwait(false);

        // CT-13: a credit note citing the first document.
        var note = Invoice(Encf34(settings.First34));
        var idDoc = (JsonObject)note["idDoc"]!;
        idDoc.Remove("sequenceDueDate");
        idDoc.Remove("paymentTerm");
        idDoc.Remove("paymentDeadline");
        idDoc.Remove("paymentFormsTable");
        idDoc["creditNoteIndicator"] = 0;
        note["informationReference"] = new JsonObject
        {
            ["ncfModified"] = Encf31(n),
            ["ncfModifiedDate"] = Today,
            ["modificationCode"] = 3,
            ["reasonForModification"] = "Prueba contractual",
        };
        var credit = await SendAsync("CT-13", $"34 {Encf34(settings.First34)} citing {Encf31(n)}", "credit-notes", note, cancellationToken).ConfigureAwait(false);
        requests += 1 + await FollowAsync("CT-13", "credit-notes", credit.Id, cancellationToken).ConfigureAwait(false);

        // CT-14: a burst — how Alanube answers many documents in a row (rate limits, codes).
        for (var i = 0; i < settings.Burst; i++)
        {
            await SendAsync("CT-14", $"burst {i + 1}/{settings.Burst}", "fiscal-invoices", Invoice(Encf31(n + 4 + i)), cancellationToken).ConfigureAwait(false);
            requests++;
        }

        return requests;
    }

    private decimal Price => decimal.Parse(settings.UnitPrice, NumberStyles.Number, CultureInfo.InvariantCulture);

    private decimal Rate => decimal.Parse(settings.ItbisRate, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Encf(string type, long number) => "E" + type + number.ToString("D10", CultureInfo.InvariantCulture);

    private static string Encf31(long number) => Encf("31", number);

    private static string Encf34(long number) => Encf("34", number);

    /// <summary>One line of 1 × the price, ITBIS at the rate — the smallest valid e-CF 31.</summary>
    private JsonObject Invoice(string encf)
    {
        var itbis = Money(Price * Rate);
        return new JsonObject
        {
            ["idDoc"] = new JsonObject
            {
                ["encf"] = encf,
                ["sequenceDueDate"] = settings.SequenceDueDate,
                ["taxAmountIndicator"] = 0,
                ["incomeType"] = 1,
                ["paymentType"] = 1,
                ["paymentFormsTable"] = new JsonArray(new JsonObject { ["paymentMethod"] = 1, ["paymentAmount"] = Money(Price) + itbis }),
            },
            ["sender"] = new JsonObject
            {
                ["rnc"] = settings.SenderRnc,
                ["companyName"] = settings.SenderName,
                ["address"] = settings.SenderAddress,
                ["internalInvoiceNumber"] = "CT-" + encf[^6..],
                ["stampDate"] = Today,
            },
            ["buyer"] = new JsonObject { ["rnc"] = settings.BuyerRnc, ["companyName"] = settings.BuyerName },
            ["totals"] = new JsonObject
            {
                ["totalTaxedAmount"] = Money(Price),
                ["i1AmountTaxed"] = Money(Price),
                ["itbisS1"] = (int)(Rate * 100m),
                ["itbisTotal"] = itbis,
                ["itbis1Total"] = itbis,
                ["totalAmount"] = Money(Price) + itbis,
            },
            ["itemDetails"] = new JsonArray(new JsonObject
            {
                ["lineNumber"] = 1,
                ["billingIndicator"] = 1,
                ["itemName"] = "PRUEBA CONTRACTUAL",
                ["goodServiceIndicator"] = 1,
                ["quantityItem"] = 1,
                ["unitMeasure"] = 43,
                ["unitPriceItem"] = Money(Price),
                ["itemAmount"] = Money(Price),
            }),
        };
    }

    private Task<Answer> SendAsync(string ct, string what, string path, JsonObject payload, CancellationToken cancellationToken)
        => SendAsync(ct, what, path, payload, cancellationToken, cancellationToken);

    private async Task<Answer> SendAsync(string ct, string what, string path, JsonObject payload, CancellationToken callToken, CancellationToken reportToken)
    {
        var started = DateTime.UtcNow;
        Answer answer;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, callToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(reportToken).ConfigureAwait(false);
            answer = new Answer((int)response.StatusCode, body, IdOf(body));
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !reportToken.IsCancellationRequested)
        {
            answer = new Answer(null, $"{ex.GetType().Name}: {ex.Message}", null);
        }

        await WriteAsync(ct, what, "POST", path, payload, answer, started, reportToken).ConfigureAwait(false);
        return answer;
    }

    /// <summary>Queries the document until FINISHED / FAILED or <see cref="EcfContractSettings.PollTimes"/> tries; returns the queries made.</summary>
    private async Task<int> FollowAsync(string ct, string path, string? id, CancellationToken cancellationToken)
    {
        if (id is null)
        {
            return 0;
        }

        for (var i = 1; i <= settings.PollTimes; i++)
        {
            await _delay(TimeSpan.FromSeconds(settings.PollSeconds), cancellationToken).ConfigureAwait(false);
            var started = DateTime.UtcNow;
            using var response = await http.GetAsync($"{path}/{id}", cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(ct, $"status {i}", "GET", $"{path}/{id}", null, new Answer((int)response.StatusCode, body, id), started, cancellationToken).ConfigureAwait(false);
            var status = StatusOf(body);
            if (status is "FINISHED" or "FAILED")
            {
                return i;
            }
        }

        return settings.PollTimes;
    }

    private async Task WriteAsync(string ct, string what, string method, string path, JsonObject? payload, Answer answer, DateTime started, CancellationToken cancellationToken)
    {
        var line = new JsonObject
        {
            ["case"] = ct,
            ["what"] = what,
            ["at"] = started.ToString("O", CultureInfo.InvariantCulture),
            ["method"] = method,
            ["path"] = path,
            ["request"] = payload?.DeepClone(),
            ["status"] = answer.Status,
            ["response"] = Parse(answer.Body),
        };
        await report.WriteLineAsync(line.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        await report.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode? Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    private static string? IdOf(string body) => Parse(body) is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;

    private static string? StatusOf(string body) => Parse(body) is JsonObject o && o["status"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The HttpClient for the sandbox with the token from the file (the token is never written anywhere).</summary>
    public static HttpClient Client(EcfContractSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var token = File.ReadAllText(settings.TokenFile).Trim();
        var client = new HttpClient { BaseAddress = new Uri(settings.BaseUrl.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
