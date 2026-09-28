using Rochell.Platform.Commands;

namespace Rochell.Sales.Opening;

/// <summary>
/// E-VS3-02b-1…5: the Controller loads the opening finished goods of a cutover from the CSV extracted from ADM (base64, kept by
/// its SHA-256): columns planta, ubicacion, producto, cantidad, documento. The batch is DRAFT; each line is valued at the
/// active standard cost of its plant's valuation area.
/// </summary>
public sealed record PrepareOpeningInventory(Guid CompanyId, Guid SessionId, string IdempotencyKey, string FileName, string ContentBase64, DateOnly CutoverDate) : ICommand;

/// <summary>E-VS3-02b-2/6/7: the Aprobador de políticas (not the preparer, with step-up) posts it the day before the cutover (OPEN-INV).</summary>
public sealed record PostOpeningInventory(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid BatchId, long ExpectedVersion) : ICommand;

/// <summary>E-VS3-02b-9: exact reversal of a whole POSTED batch, with a reason, while none of its lots moved and INV-MOV is open.</summary>
public sealed record ReverseOpeningInventory(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid BatchId, long ExpectedVersion, string Reason) : ICommand;
