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
