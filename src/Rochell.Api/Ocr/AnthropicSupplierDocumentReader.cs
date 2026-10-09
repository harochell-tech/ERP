using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Procurement.SupplierDocuments;

namespace Rochell.Api.Ocr;

/// <summary>E-OCR-5, E-OCR1-01-10: the switch and the key of reading invoices by AI — server settings, never in the repository.</summary>
public sealed class OcrSettings
{
    /// <summary>OFF (default), ANTHROPIC, or SIMULATED (Development / Test only).</summary>
    public string Mode { get; set; } = OcrModes.Off;

    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Anthropic's API key — a server secret; or a file holding it (<c>secrets/ocr/anthropic-key</c>).</summary>
    public string? ApiKey { get; set; }

    public string? ApiKeyFile { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

    public bool Enabled => Mode is OcrModes.Anthropic or OcrModes.Simulated;
}

public static class OcrModes
{
    public const string Off = "OFF";
    public const string Anthropic = "ANTHROPIC";
    public const string Simulated = "SIMULATED";
}

/// <summary>
/// OCR1-04 (E-OCR1-04-1/2): Anthropic's Messages API with the file (image or PDF) and a forced tool call, so the answer is the
/// <see cref="InvoiceReadingFormat"/> shape. A failed call is retried once; nothing but the file and the instructions is sent.
/// </summary>
public sealed class AnthropicSupplierDocumentReader(HttpClient http, OcrSettings settings, ILogger<AnthropicSupplierDocumentReader> logger) : ISupplierDocumentReader
{
    private const int MaxTokens = 4096; // type-limit: the reading's answer
    private const int Attempts = 2; // E-OCR1-04-2: one retry

    public async Task<ReaderOutcome> ReadAsync(byte[] content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["max_tokens"] = MaxTokens,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "record_invoice",
                ["description"] = "Registra lo que la factura del proveedor muestra impreso.",
                ["input_schema"] = InvoiceReadingFormat.Schema(),
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "record_invoice" },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = contentType == "application/pdf" ? "document" : "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = contentType, ["data"] = Convert.ToBase64String(content) },
                    },
                    new JsonObject { ["type"] = "text", ["text"] = InvoiceReadingFormat.Instructions }),
            }),
        }.ToJsonString();

        string? failure = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), "v1/messages"));
            request.Headers.Add("x-api-key", settings.ApiKey ?? throw new InvalidOperationException("Ocr:ApiKey is not configured."));
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.Timeout);
                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    failure = $"HTTP {(int)response.StatusCode}";
                    logger.LogWarning("Invoice reading failed with {Status} (attempt {Attempt}).", (int)response.StatusCode, attempt);
                    continue;
                }

                var answer = JsonNode.Parse(text) as JsonObject;
                var usage = answer?["usage"] as JsonObject;
                var tool = (answer?["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["type"]?.GetValue<string>() == "tool_use");
                var reading = tool?["input"] is JsonObject input ? InvoiceReadingFormat.Parse(input) : null;
                return new ReaderOutcome(
                    true,
                    reading is { IssuerRnc: null, FiscalNumber: null, Total: null, Lines.Count: 0 } ? null : reading,
                    answer?["model"]?.GetValue<string>() ?? settings.Model,
                    usage?["input_tokens"]?.GetValue<int>(),
                    usage?["output_tokens"]?.GetValue<int>(),
                    reading is null ? "La respuesta no trajo la lectura." : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                failure = ex.GetType().Name;
                logger.LogWarning("Invoice reading failed: {Error} (attempt {Attempt}).", ex.GetType().Name, attempt);
            }
        }

        return new ReaderOutcome(false, null, settings.Model, null, null, failure ?? "sin respuesta");
    }
}
