using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochell.Tax.Ecf;

/// <summary>
/// OCR1-02 (E-OCR-2/3): Alanube's reception API — <c>GET /received-documents</c>, <c>GET /received-documents/{id}</c> and
/// <c>POST /received-documents/{id}/commercial-response</c>. The list items' shape is not published (docs/fiscal/alanube-api.md §13.18):
/// they are read with the detail's field names, and whatever is missing is taken from the detail.
/// </summary>
public sealed partial class AlanubeProvider : IEcfReception
{
    public async Task<ReceivedListOutcome> ListReceivedAsync(ReceivedListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"received-documents?status={request.Status}&commercialResponse={request.CommercialResponse}&start={request.Start:yyyy-MM-dd}&end={request.End:yyyy-MM-dd}&page={request.Page}&limit={request.Limit}");
        using var message = Request(HttpMethod.Get, query);
        var (status, body, failure) = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (failure is not null || status is null || (int)status >= 500)
        {
            return new ReceivedListOutcome(false, status is null ? null : (int)status, [], null, failure ?? Truncate(body));
        }

        if (status != HttpStatusCode.OK)
        {
            var (code, text) = ReadError(body);
            return new ReceivedListOutcome(false, (int)status, [], code, text ?? Truncate(body));
        }

        try
        {
            var documents = JsonNode.Parse(body)?["documents"] as JsonArray ?? [];
            return new ReceivedListOutcome(true, (int)status, documents.OfType<JsonObject>().Select(ReadReceived).OfType<ReceivedDocument>().ToList(), null, null);
        }
        catch (JsonException)
        {
            return new ReceivedListOutcome(false, (int)status, [], null, "Alanube answered the list with something that is not JSON.");
        }
    }

    public async Task<ReceivedGetOutcome> GetReceivedAsync(string providerId, CancellationToken cancellationToken)
    {
        using var message = Request(HttpMethod.Get, $"received-documents/{Uri.EscapeDataString(providerId)}");
        var (status, body, failure) = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (failure is not null || status is null || (int)status >= 500)
        {
            return new ReceivedGetOutcome(QueryKind.Transient, status is null ? null : (int)status, null, null, failure ?? Truncate(body));
        }

        if (status == HttpStatusCode.NotFound)
        {
            var (code, text) = ReadError(body);
            return new ReceivedGetOutcome(QueryKind.NotFound, (int)status, null, code, text);
        }

        ReceivedDocument? document = null;
        try
        {
            document = status == HttpStatusCode.OK && JsonNode.Parse(body) is JsonObject o ? ReadReceived(o) : null;
        }
        catch (JsonException)
        {
        }

        if (document is null)
        {
            var (code, text) = ReadError(body);
            return new ReceivedGetOutcome(QueryKind.Transient, (int)status, null, code, text ?? Truncate(body));
        }

        return new ReceivedGetOutcome(QueryKind.Found, (int)status, document, null, null);
    }

    public async Task<SubmitOutcome> RespondAsync(string providerId, bool accept, string? reason, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["commercialResponse"] = accept ? ReceivedStatuses.Accepted : ReceivedStatuses.Rejected };
        if (!accept)
        {
            payload["notAcceptedDetail"] = reason;
        }

        using var message = Request(HttpMethod.Post, $"received-documents/{Uri.EscapeDataString(providerId)}/commercial-response");
        message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var (status, body, failure) = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (failure is not null || status is null || (int)status >= 500)
        {
            return new SubmitOutcome(SubmitKind.Transient, status is null ? null : (int)status, null, null, null, failure ?? Truncate(body));
        }

        if (status is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var id = JsonNode.Parse(body)?["commercialApprovalInfo"]?["id"]?.ToString();
            return new SubmitOutcome(SubmitKind.Registered, (int)status, id, null, null, null);
        }

        var (errorCode, errorText) = ReadError(body);
        return new SubmitOutcome(SubmitKind.Invalid, (int)status, null, null, errorCode, errorText ?? Truncate(body));
    }

    /// <summary>A received document from Alanube's JSON; null without an id.</summary>
    public static ReceivedDocument? ReadReceived(JsonObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (Text(o, "id") is not { } id)
        {
            return null;
        }

        return new ReceivedDocument(
            id,
            Text(o, "issuerIdentification"),
            Text(o, "buyerIdentification"),
            Text(o, "documentType"),
            Text(o, "documentNumber"),
            Text(o, "status"),
            Text(o, "errorMsg"),
            Text(o, "commercialResponse"),
            DateTimeOffset.TryParse(Text(o, "signatureDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var signed) ? signed : null,
            Text(o, "totalAmount"),
            Text(o, "xml"));
    }
}
