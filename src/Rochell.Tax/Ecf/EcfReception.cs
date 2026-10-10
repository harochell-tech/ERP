using System.Diagnostics;
using Rochell.Platform.Commands;

namespace Rochell.Tax.Ecf;

// OCR1-02 (E-OCR-2/3, E-OCR1-02-1…10): the e-CF that suppliers send to the company, as Alanube keeps them (docs/fiscal/alanube-api.md
// §10): the list, one document with its XML, and the commercial response to the DGII. Procurement turns them into captured supplier
// documents.

/// <summary>Alanube's received-document statuses and commercial responses.</summary>
public static class ReceivedStatuses
{
    public const string Received = "RECEIVED";
    public const string NotReceived = "NOT_RECEIVED";
    public const string NotDeclared = "NOT_DECLARED";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
}

/// <summary>One page of <c>GET /received-documents</c>: issuer RNC, status, commercial response and dates (start / end inclusive).</summary>
public sealed record ReceivedListRequest(string Status, string CommercialResponse, DateOnly Start, DateOnly End, int Page, int Limit);

/// <summary>One received document as the list or the detail describes it; <see cref="Xml"/> only on the detail (the XML itself, or a link).</summary>
public sealed record ReceivedDocument(
    string Id,
    string? IssuerRnc,
    string? BuyerRnc,
    string? DocumentType,
    string? DocumentNumber,
    string? Status,
    string? ErrorMessage,
    string? CommercialResponse,
    DateTimeOffset? SignatureDate,
    string? TotalAmount,
    string? Xml);

public sealed record ReceivedListOutcome(bool Ok, int? HttpStatus, IReadOnlyList<ReceivedDocument> Items, string? Code, string? Message);

public sealed record ReceivedGetOutcome(QueryKind Kind, int? HttpStatus, ReceivedDocument? Document, string? Code, string? Message);

/// <summary>E-OCR-2/3: what suppliers sent the company through Alanube.</summary>
public interface IEcfReception
{
    /// <summary>SANDBOX, PRODUCTION or SIMULATED — recorded on every call.</summary>
    string Mode { get; }

    Task<ReceivedListOutcome> ListReceivedAsync(ReceivedListRequest request, CancellationToken cancellationToken);

    Task<ReceivedGetOutcome> GetReceivedAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>E-OCR-3: accepts or rejects (with its reason) a received e-CF before the DGII.</summary>
    Task<SubmitOutcome> RespondAsync(string providerId, bool accept, string? reason, CancellationToken cancellationToken);

    /// <summary>A file Alanube links to (the received XML), without the token.</summary>
    Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken);
}

/// <summary>The calls of reception go to <c>tax.ecf_call</c> like the issuing ones — never the token.</summary>
public static class EcfCallLog
{
    public const string ReceivedList = "RECEIVED_LIST";
    public const string ReceivedGet = "RECEIVED_GET";
    public const string CommercialResponse = "COMMERCIAL_RESPONSE";

    public static Task RecordAsync(
        CommandContext context, string mode, string operation, int? httpStatus, string outcome, string? code, string? message, Stopwatch watch, CancellationToken cancellationToken)
        => Health.CallAsync(context, mode, null, operation, httpStatus, outcome, code, message, watch, cancellationToken);
}
