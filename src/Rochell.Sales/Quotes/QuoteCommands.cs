using Rochell.Platform.Commands;

namespace Rochell.Sales.Quotes;

/// <summary>
/// A quote line (E-QUO1-02-2): finished good, unit and quantity; <paramref name="UnitPrice"/> null takes the list price, a lower one
/// is a special price that needs approval (E-QUO1-3).
/// </summary>
public sealed record QuoteLineInput(Guid ItemId, string Uom, decimal Quantity, decimal? UnitPrice = null);

/// <summary>E-QUO1-02-1…4: the Vendedor quotes a DRAFT or ACTIVE customer; the quote is COT-… DRAFT, priced from the list in force.</summary>
public sealed record CreateQuote(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, Guid PlantId, DateOnly ValidUntil, string DeliveryTermCode, string? SiteAddress,
    string? CustomerRef, string? Notes, IReadOnlyList<QuoteLineInput> Lines) : ICommand;

/// <summary>Replaces a DRAFT quote's header and lines (a new lines version; an earlier price approval no longer covers them).</summary>
public sealed record UpdateDraftQuote(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion, Guid PlantId, DateOnly ValidUntil, string DeliveryTermCode,
    string? SiteAddress, string? CustomerRef, string? Notes, IReadOnlyList<QuoteLineInput> Lines) : ICommand;

/// <summary>E-QUO1-02-5: DRAFT → PENDING_APPROVAL, only when a line is below its list price.</summary>
public sealed record SubmitQuoteForApproval(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion) : ICommand;

/// <summary>E-QUO1-02-6: PENDING_APPROVAL → DRAFT with the current lines' special prices approved (not by the author; step-up).</summary>
public sealed record ApproveQuotePrices(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion) : ICommand;

/// <summary>E-QUO1-02-5: PENDING_APPROVAL → DRAFT without approval, with a reason.</summary>
public sealed record ReturnQuoteToDraft(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-QUO1-02-7: DRAFT → SENT (frozen): special prices approved for the current lines, not expired.</summary>
public sealed record SendQuote(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion) : ICommand;

/// <summary>E-QUO1-02-8: SENT → LOST, with a reason.</summary>
public sealed record MarkQuoteLost(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-QUO1-02-8: DRAFT or SENT → CANCELLED, with a reason.</summary>
public sealed record CancelQuote(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-QUO1-02-9: a new DRAFT from any quote — same customer, header and quoted prices, list prices in force, a new validity.</summary>
public sealed record CopyQuote(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, DateOnly ValidUntil) : ICommand;

/// <summary>E-QUO1-03-1…3: a SENT, unexpired quote of an ACTIVE customer becomes one DRAFT order at its quoted prices (QUOTED_AS).</summary>
public sealed record ConvertQuote(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, long ExpectedVersion) : ICommand;
