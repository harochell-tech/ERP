using Rochell.Platform.Commands;

namespace Rochell.Sales.Customers;

/// <summary>
/// E-VS3-01-1, E-VS3-02-3: the Vendedor creates a customer (DRAFT). One party per RNC: an existing party with that RNC (a
/// supplier) becomes a customer instead; the result names the party.
/// </summary>
public sealed record CreateCustomer(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string Rnc, string LegalName, string? Phone = null, string? Email = null, string? Address = null) : ICommand;

/// <summary>E-VS3-02-4: RNC and legal name only while the party is DRAFT; phone, e-mail and address at any time.</summary>
public sealed record UpdateCustomer(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion, string Rnc, string LegalName, string? Phone, string? Email, string? Address) : ICommand;

/// <summary>E-VS3-02-5: Crédito activates a customer that has approved terms (and its party, when still DRAFT).</summary>
public sealed record ActivateCustomer(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion) : ICommand;

/// <summary>E-VS3-02-6: the customer's single DRAFT terms version, created or replaced (Crédito).</summary>
public sealed record PrepareCustomerTerms(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, int PaymentTermsDays, decimal CreditLimit, bool CreditHold) : ICommand;

/// <summary>E-VS3-02-6: the Controller approves the DRAFT (not the preparer, with step-up); in force from the approval date.</summary>
public sealed record ApproveCustomerTerms(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TermsVersionId) : ICommand;
