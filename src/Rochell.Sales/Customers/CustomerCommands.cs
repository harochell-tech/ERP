using Rochell.Platform.Commands;

namespace Rochell.Sales.Customers;

/// <summary>
/// E-VS3-01-1, E-VS3-02-3: the Vendedor creates a customer (DRAFT). One party per RNC: an existing party with that RNC (a
/// supplier) becomes a customer instead; the result names the party.
/// </summary>
/// <remarks>E-IMP-6: <paramref name="Emails"/> (at most ten, the first is the principal one) replaces <paramref name="Email"/> when given.</remarks>
public sealed record CreateCustomer(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string Rnc, string LegalName, string? Phone = null, string? Email = null, string? Address = null,
    IReadOnlyList<string>? Emails = null) : ICommand;

/// <summary>E-VS3-02-4: RNC and legal name only while the party is DRAFT; phone, e-mail and address at any time.</summary>
/// <remarks>
/// E-IMP-6: <paramref name="Emails"/> replaces the customer's whole list; without it <paramref name="Email"/> replaces only the
/// principal e-mail and the others stay.
/// </remarks>
public sealed record UpdateCustomer(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion, string Rnc, string LegalName, string? Phone, string? Email, string? Address,
    IReadOnlyList<string>? Emails = null) : ICommand;

/// <summary>E-VS3-02-5: Crédito activates a customer that has approved terms (and its party, when still DRAFT).</summary>
public sealed record ActivateCustomer(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion) : ICommand;

/// <summary>
/// E-VS3-02-6: the customer's single DRAFT terms version, created or replaced (Crédito). E-PRC1-6, E-PRS-02-3: with its price list —
/// <paramref name="PriceListId"/> when given (ACTIVE), else the list the customer already has (GENERAL for a new customer).
/// </summary>
public sealed record PrepareCustomerTerms(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, int PaymentTermsDays, decimal CreditLimit, bool CreditHold, Guid? PriceListId = null) : ICommand;

/// <summary>E-VS3-02-6: the Controller approves the DRAFT (not the preparer, with step-up); in force from the approval date.</summary>
public sealed record ApproveCustomerTerms(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TermsVersionId) : ICommand;

/// <summary>
/// E-IMP-1…11: loads the ADM Cloud customer export (.xlsx or .csv, base64, ≤ 5 MB). Every row with a valid RNC or cédula that is
/// not yet a customer becomes a DRAFT customer (the supplier with that RNC is flagged, E-VS3-02-3) with its phone and e-mails and
/// a DRAFT terms version: the file's payment term (cash when empty, E-IMP-01-7) and credit limit (0 without the optional column
/// "Límite de Crédito", E-IMP-8). The result lists every row (what <c>PreviewCustomerImport</c> showed).
/// </summary>
public sealed record ImportCustomers(Guid CompanyId, Guid SessionId, string IdempotencyKey, string FileName, string ContentBase64) : ICommand;

/// <summary>E-IMP-01-6: approves up to 500 DRAFT terms versions; one the approver prepared, or not DRAFT, is reported and skipped.</summary>
public sealed record ApproveCustomerTermsBatch(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<Guid> TermsVersionIds) : ICommand;

/// <summary>E-IMP-7, E-IMP-01-6: activates up to 500 DRAFT customers; one without approved terms is reported and skipped.</summary>
public sealed record ActivateCustomers(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<Guid> PartyIds) : ICommand;
