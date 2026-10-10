using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochell.Tax.Ecf;

/// <summary>
/// E-VS4-3/4: Alanube's DOM API (docs/fiscal/alanube-api.md) — POST /&lt;type&gt; to issue, GET /&lt;type&gt;/{id} for the status, and the
/// signed XML / PDF from the links it returns. Every answer is classified, never thrown: a network error, a timeout or a 5xx is an
/// unknown outcome (Transient); «e-NCF already used / in process» is a Duplicate, with Alanube's id when the message carries it.
/// </summary>
public sealed partial class AlanubeProvider(HttpClient http, EcfSettings settings) : IEcfProvider
{
    private static readonly string[] DuplicateCodes = ["AP3001", "AP3010", "AP3011"];

    public string Mode => settings.Mode;

    /// <summary>E-VS4-2: the endpoint of each e-CF type in scope.</summary>
    public static string PathOf(string ecfType) => ecfType switch
    {
        "31" => "fiscal-invoices",
        "32" => "invoices",
        "34" => "credit-notes",
        "44" => "special-regimes",
        _ => throw new ArgumentOutOfRangeException(nameof(ecfType), ecfType, "e-CF type out of scope (E-VS4-2)."),
    };

    public async Task<SubmitOutcome> SubmitAsync(string ecfType, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = Request(HttpMethod.Post, PathOf(ecfType));
        request.Content = new StringContent(ForSandbox(payload, "sender", "rnc").ToJsonString(), Encoding.UTF8, "application/json");
        var (status, body, failure) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (failure is not null || status is null || (int)status >= 500)
        {
            return new SubmitOutcome(SubmitKind.Transient, status is null ? null : (int)status, null, null, null, failure ?? Truncate(body));
        }

        if (status is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var document = ReadDocument(body);
            return document is null
                ? new SubmitOutcome(SubmitKind.Transient, (int)status, null, null, null, "Alanube answered without a document id.")
                : new SubmitOutcome(SubmitKind.Registered, (int)status, document.ProviderId, document, null, null);
        }

        var (code, message) = ReadError(body);
        if (code is not null && DuplicateCodes.Contains(code))
        {
            var id = message is null ? null : InProcessId().Match(message) is { Success: true } m ? m.Groups["id"].Value : null;
            return new SubmitOutcome(SubmitKind.Duplicate, (int)status, id, null, code, message);
        }

        return new SubmitOutcome(SubmitKind.Invalid, (int)status, null, null, code, message ?? Truncate(body));
    }

    /// <summary>E-VS4-12: <c>POST /cancellations</c> — annuls unused e-NCF ranges with the DGII (ANECF).</summary>
    public async Task<SubmitOutcome> CancelAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = Request(HttpMethod.Post, "cancellations");
        request.Content = new StringContent(ForSandbox(payload, "header", "rncSender").ToJsonString(), Encoding.UTF8, "application/json");
        var (status, body, failure) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (failure is not null || status is null || (int)status >= 500)
        {
            return new SubmitOutcome(SubmitKind.Transient, status is null ? null : (int)status, null, null, null, failure ?? Truncate(body));
        }

        if (status is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var id = JsonNode.Parse(body) is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
            return id is null
                ? new SubmitOutcome(SubmitKind.Transient, (int)status, null, null, null, "Alanube answered without a cancellation id.")
                : new SubmitOutcome(SubmitKind.Registered, (int)status, id, null, null, null);
        }

        var (code, message) = ReadError(body);
        return new SubmitOutcome(SubmitKind.Invalid, (int)status, null, null, code, message ?? Truncate(body));
    }

    public async Task<QueryOutcome> QueryAsync(string ecfType, string providerId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"{PathOf(ecfType)}/{Uri.EscapeDataString(providerId)}");
        var (status, body, failure) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (failure is not null || status is null || (int)status >= 500)
        {
            return new QueryOutcome(QueryKind.Transient, status is null ? null : (int)status, null, null, failure ?? Truncate(body));
        }

        if (status == HttpStatusCode.NotFound)
        {
            var (code, message) = ReadError(body);
            return new QueryOutcome(QueryKind.NotFound, (int)status, null, code, message);
        }

        var document = status == HttpStatusCode.OK ? ReadDocument(body) : null;
        if (document is null)
        {
            var (code, message) = ReadError(body);
            return new QueryOutcome(QueryKind.Transient, (int)status, null, code, message ?? Truncate(body));
        }

        return new QueryOutcome(QueryKind.Found, (int)status, document, null, null);
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        // The XML / PDF links are pre-signed storage URLs: no Alanube token goes with them.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// E-VS4-06-1: in Sandbox with <see cref="EcfSettings.SandboxSenderRnc"/> set, a copy of the payload whose sender RNC is the test
    /// company's — the same JSON kind (number or text) the field already had. Anywhere else the payload goes as it is.
    /// </summary>
    private JsonObject ForSandbox(JsonObject payload, string parent, string field)
    {
        if (settings.Mode != EcfModes.Sandbox || string.IsNullOrWhiteSpace(settings.SandboxSenderRnc) || payload[parent] is not JsonObject)
        {
            return payload;
        }

        var copy = (JsonObject)payload.DeepClone();
        var holder = (JsonObject)copy[parent]!;
        var rnc = settings.SandboxSenderRnc.Trim();
        holder[field] = holder[field] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && long.TryParse(rnc, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : rnc;
        return copy;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var baseUrl = (settings.BaseUrl ?? throw new InvalidOperationException("Rochell:Ecf:BaseUrl is not configured.")).TrimEnd('/') + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token ?? throw new InvalidOperationException("Rochell:Ecf:Token is not configured."));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<(HttpStatusCode? Status, string Body, string? Failure)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.CallTimeout);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, string.Empty, $"No answer within {settings.CallTimeout:c}.");
        }
        catch (HttpRequestException ex)
        {
            return (null, string.Empty, ex.Message);
        }
    }

    /// <summary>A document from a 201 / 200 body; null without an id.</summary>
    public static ProviderDocument? ReadDocument(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject o || Text(o, "id") is not { } id)
        {
            return null;
        }

        var error = o["error"] as JsonObject;
        return new ProviderDocument(
            id,
            Text(o, "status") ?? string.Empty,
            Text(o, "legalStatus"),
            Text(o, "trackId"),
            Text(o, "securityCode"),
            DateTimeOffset.TryParse(Text(o, "signatureDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var signed) ? signed : null,
            Text(o, "documentStampUrl"),
            Text(o, "xml"),
            Text(o, "pdf"),
            o["governmentResponse"]?.DeepClone(),
            error is null ? null : Text(error, "code"),
            error is null ? null : Text(error, "message"));
    }

    /// <summary>The first error code and message of either error format (docs/fiscal/alanube-api.md §1).</summary>
    public static (string? Code, string? Message) ReadError(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o)
            {
                return (null, null);
            }

            if (o["errors"] is JsonArray { Count: > 0 } errors && errors[0] is JsonObject first)
            {
                return (Text(first, "code"), Text(first, "message"));
            }

            var message = o["message"] switch
            {
                JsonArray a => string.Join("; ", a.Select(x => x?.ToString())),
                JsonNode n => n.ToString(),
                null => null,
            };
            return (Text(o, "code"), message);
        }
        catch (JsonException)
        {
            return (null, Truncate(body));
        }
    }

    private static string? Text(JsonObject o, string name)
        => o[name] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonNode n => n.ToJsonString(),
        };

    private static string Truncate(string body) => body.Length <= 500 ? body : body[..500];

    [GeneratedRegex("id:\\s*(?<id>[0-9A-HJKMNP-TV-Z]{26})", RegexOptions.IgnoreCase)]
    private static partial Regex InProcessId();
}
