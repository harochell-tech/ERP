using Rochell.Platform.Commands;

namespace Rochell.Tax.Authorizations;

/// <summary>A scope line: a finished good in a sale unit (its base unit or one with a conversion in force), quantity and net amount (DOP, 2 decimals).</summary>
public sealed record AuthorizationLineInput(Guid ItemId, string Uom, decimal Quantity, decimal NetAmount);

/// <summary>
/// E-FIS1-02-2: Crédito or Facturación registers a DRAFT authorization from a DGII exemption certificate of an ACTIVE customer's
/// CONFOTUR project, with its scope.
/// </summary>
public sealed record RegisterFiscalAuthorization(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    string CertificateNo,
    DateOnly IssuedOn,
    DateOnly? ValidUntil,
    string ProjectName,
    string ConfoturResolutionNo,
    DateOnly? ProjectTermEndsOn,
    Guid? SalesOrderId,
    IReadOnlyList<AuthorizationLineInput> Lines) : ICommand;

/// <summary>E-FIS1-02-2: replaces the data and the scope of a DRAFT authorization.</summary>
public sealed record UpdateDraftAuthorization(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid AuthorizationId,
    long ExpectedVersion,
    string CertificateNo,
    DateOnly IssuedOn,
    DateOnly? ValidUntil,
    string ProjectName,
    string ConfoturResolutionNo,
    DateOnly? ProjectTermEndsOn,
    Guid? SalesOrderId,
    IReadOnlyList<AuthorizationLineInput> Lines) : ICommand;

/// <summary>E-FIS1-02-3: a document (CERTIFICADO_DGII, RESOLUCION_CONFOTUR, LISTA_MATERIALES, PROFORMA) by reference and SHA-256.</summary>
public sealed record AttachAuthorizationDocument(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, string Kind, string EvidenceRef, string EvidenceSha256)
    : ICommand;

/// <summary>E-FIS1-02-4: DRAFT → PENDING_VERIFICATION, with at least one scope line and the DGII certificate attached.</summary>
public sealed record SubmitForVerification(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, long ExpectedVersion) : ICommand;

/// <summary>E-FIS1-02-5: the Especialista fiscal verifies (step-up, not the registrar): PENDING_VERIFICATION → ACTIVE, unless expired.</summary>
public sealed record VerifyAuthorization(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, long ExpectedVersion) : ICommand;

/// <summary>E-FIS1-02-5: PENDING_VERIFICATION → DRAFT with a reason, to be corrected.</summary>
public sealed record ReturnAuthorizationToDraft(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-FIS1-02-5: PENDING_VERIFICATION → REJECTED with a reason (terminal).</summary>
public sealed record RejectAuthorization(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-FIS1-02-6: ACTIVE → SUSPENDED (reason, step-up); covered sales stop until it is reactivated.</summary>
public sealed record SuspendAuthorization(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-FIS1-02-6: SUSPENDED → ACTIVE (reason, step-up), unless expired.</summary>
public sealed record ReactivateAuthorization(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AuthorizationId, long ExpectedVersion, string Reason) : ICommand;
