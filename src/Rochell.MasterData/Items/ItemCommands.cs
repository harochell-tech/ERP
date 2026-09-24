using Rochell.Platform.Commands;

namespace Rochell.MasterData.Items;

/// <summary>Creates a raw material in DRAFT with its base unit of measure and category (E-PR04-7).</summary>
public sealed record CreateRawMaterial(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    string Code,
    string Description,
    string BaseUom,
    string ItemCategory) : ICommand;

/// <summary>
/// Defines "1 <paramref name="FromUom"/> = <paramref name="Factor"/> base UOM" from <paramref name="EffectiveFrom"/> on
/// (E-PR04-8: always towards the base UOM; closes the open conversion; never retroactive).
/// </summary>
public sealed record DefineUomConversion(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid ItemId,
    string FromUom,
    decimal Factor,
    DateOnly EffectiveFrom) : ICommand;

/// <summary>DRAFT → ACTIVE (E-PR04-3).</summary>
public sealed record ActivateItem(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ItemId, long ExpectedVersion) : ICommand;
