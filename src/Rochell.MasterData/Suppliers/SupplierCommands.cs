using Rochell.Platform.Commands;

namespace Rochell.MasterData.Suppliers;

/// <summary>Creates a local supplier in DRAFT (E-PR04-3, E-PR04-6).</summary>
public sealed record CreateSupplier(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Rnc, string LegalName) : ICommand;

/// <summary>Changes a DRAFT supplier (E-PR04-4). <paramref name="ExpectedVersion"/> enables optimistic concurrency.</summary>
public sealed record UpdateSupplier(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion, string Rnc, string LegalName) : ICommand;

/// <summary>DRAFT → ACTIVE; the Controller's activation is the approval in VS#1 (E-PR04-3).</summary>
public sealed record ActivateSupplier(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion) : ICommand;

/// <summary>E-VS3-17 (b), E-VS3-02-10: the supplier's payment term in days (0–365, null clears it), at any time.</summary>
public sealed record SetSupplierPaymentTerms(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion, int? PaymentTermsDays) : ICommand;

/// <summary>
/// E-IMP-1…11: loads the ADM Cloud supplier export (.xlsx or .csv, base64, ≤ 5 MB). Every row with a valid RNC that is new to
/// the company becomes a DRAFT supplier with its phone, e-mails and payment term; the others are reported, not loaded. The result
/// lists every row (what <c>PreviewSupplierImport</c> showed).
/// </summary>
public sealed record ImportSuppliers(Guid CompanyId, Guid SessionId, string IdempotencyKey, string FileName, string ContentBase64) : ICommand;

/// <summary>E-IMP-7, E-IMP-01-5: DRAFT → ACTIVE for up to 500 suppliers; one that is not DRAFT is reported and skipped.</summary>
public sealed record ActivateSuppliers(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<Guid> PartyIds) : ICommand;

/// <summary>E-IMP-6, E-IMP-01-2: the supplier's phone and e-mails (at most ten; the first is the principal one), at any time.</summary>
public sealed record SetSupplierContact(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, long ExpectedVersion, string? Phone, IReadOnlyList<string>? Emails) : ICommand;
