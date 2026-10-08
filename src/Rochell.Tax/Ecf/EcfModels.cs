using System.Text.Json.Nodes;

namespace Rochell.Tax.Ecf;

// VS4-02 (E-VS4-3…11, E-VS4-02-1…7): the e-CF gateway's vocabulary — its modes, statuses, errors, and the provider it talks to.

/// <summary>E-VS4-11, E-VS4-01-7: server settings (never in the repository): Off by default; Sandbox / Production call Alanube; Simulated only in tests and the dev stack.</summary>
public sealed class EcfSettings
{
    public string Mode { get; set; } = EcfModes.Off;

    /// <summary>Alanube's base URL, e.g. https://sandbox.alanube.co/dom/v1/.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Alanube's Bearer token — a server secret.</summary>
    public string? Token { get; set; }

    /// <summary>E-VS4-04-7: a file on the server holding the token (read when <see cref="Token"/> is not set).</summary>
    public string? TokenFile { get; set; }

    /// <summary>A file on the server holding the webhook secret (read when it exists and <see cref="WebhookSecret"/> is not set).</summary>
    public string? WebhookSecretFile { get; set; }

    /// <summary>E-VS4-02-5: the secret Alanube sends in <see cref="EcfModes.WebhookHeader"/> with each webhook.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>E-VS4-02-1: how often the worker takes the queue.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>E-VS4-02-3: a call that takes longer has an unknown outcome.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool Enabled => Mode is EcfModes.Sandbox or EcfModes.Production or EcfModes.Simulated;
}

/// <summary>E-VS4-03-1: whether this deployment sends e-CF through the gateway (its mode is not Off). Off: every document takes the manual channel.</summary>
public sealed record EcfSwitch(bool On)
{
    public static readonly EcfSwitch Off = new(false);
}

public static class EcfModes
{
    public const string Off = "OFF";
    public const string Sandbox = "SANDBOX";
    public const string Production = "PRODUCTION";
    public const string Simulated = "SIMULATED";
    public const string WebhookHeader = "X-Rochell-Ecf-Secret";
}

public static class EcfStatuses
{
    public const string Pending = "PENDING";
    public const string Submitted = "SUBMITTED";
    public const string Unknown = "UNKNOWN_OUTCOME";
    public const string Accepted = "ACCEPTED";
    public const string AcceptedConditional = "ACCEPTED_CONDITIONAL";
    public const string Rejected = "REJECTED";
    public const string RequiresAction = "REQUIRES_ACTION";
    public const string Contingency = "CONTINGENCY";

    public static bool IsFinal(string status) => status is Accepted or AcceptedConditional or Rejected;
}

public static class EcfErrors
{
    public const string SeriesMissing = "ECF_SERIES_MISSING";
    public const string SeriesExpired = "ECF_SERIES_EXPIRED";
    public const string SeriesInvalid = "ECF_SERIES_INVALID";
    public const string SeriesNotFound = "ECF_SERIES_NOT_FOUND";
    public const string DocumentNotFound = "ECF_DOCUMENT_NOT_FOUND";
    public const string GatewayOff = "ECF_GATEWAY_OFF";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string ApproverIsPreparer = "APPROVER_IS_CREATOR";
    public const string IssuerIncomplete = "ECF_ISSUER_INCOMPLETE";
    public const string PayloadInvalid = "ECF_PAYLOAD_INVALID";
    public const string ResolutionInvalid = "ECF_RESOLUTION_INVALID";
    public const string CancelFailed = "ECF_CANCEL_FAILED";
}

/// <summary>What a provider answered to an issuance.</summary>
public enum SubmitKind
{
    /// <summary>The provider holds the document (201), maybe already with its DGII answer (e-CF 32 under the summary amount).</summary>
    Registered,

    /// <summary>The provider already holds this e-NCF (AP3001 / AP3010 / AP3011); <see cref="SubmitOutcome.ProviderId"/> when it said which.</summary>
    Duplicate,

    /// <summary>The provider refused the content (400): a data error to correct.</summary>
    Invalid,

    /// <summary>No clear answer: network error, timeout or 5xx — the outcome is unknown (E-VS4-4).</summary>
    Transient,
}

public sealed record SubmitOutcome(SubmitKind Kind, int? HttpStatus, string? ProviderId, ProviderDocument? Document, string? Code, string? Message);

/// <summary>A provider document as Alanube describes it (GET /&lt;type&gt;/{id}).</summary>
public sealed record ProviderDocument(
    string ProviderId,
    string Status,
    string? LegalStatus,
    string? TrackId,
    string? SecurityCode,
    DateTimeOffset? SignatureDate,
    string? StampUrl,
    string? XmlUrl,
    string? PdfUrl,
    JsonNode? GovernmentResponse,
    string? ErrorCode,
    string? ErrorMessage);

public enum QueryKind
{
    Found,
    NotFound,
    Transient,
}

public sealed record QueryOutcome(QueryKind Kind, int? HttpStatus, ProviderDocument? Document, string? Code, string? Message);

/// <summary>E-VS4-3: the e-CF provider (Alanube, or the simulated one).</summary>
public interface IEcfProvider
{
    /// <summary>SANDBOX, PRODUCTION or SIMULATED — recorded on every call.</summary>
    string Mode { get; }

    Task<SubmitOutcome> SubmitAsync(string ecfType, JsonObject payload, CancellationToken cancellationToken);

    Task<QueryOutcome> QueryAsync(string ecfType, string providerId, CancellationToken cancellationToken);

    Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken);

    /// <summary>E-VS4-12: annuls unused e-NCF ranges with the DGII.</summary>
    Task<SubmitOutcome> CancelAsync(JsonObject payload, CancellationToken cancellationToken);
}

/// <summary>
/// E-VS4-01-2/3: told when an e-CF of its kind (INVOICE, CREDIT_NOTE) reaches an answer or needs attention, inside the same transaction,
/// so the invoice or credit note follows (Sales implements it, VS4-03).
/// </summary>
public interface IEcfSourceUpdater
{
    string SourceKind { get; }

    Task OnStatusAsync(Rochell.Platform.Commands.CommandContext context, EcfDocumentSnapshot document, CancellationToken cancellationToken);
}

public sealed record EcfDocumentSnapshot(
    Guid DocumentId, string SourceKind, Guid SourceId, string EcfType, string Encf, string Status, string? SecurityCode, string? StampUrl, DateTimeOffset? SignatureDate,
    string? Reason);
