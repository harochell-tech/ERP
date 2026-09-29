using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Recipes;

/// <summary>A material of a recipe: quantity per batch in the material's base unit.</summary>
public sealed record RecipeLineInput(Guid MaterialItemId, decimal QtyPerBatch);

/// <summary>
/// E-MFG1-01-2, E-MFG1-02-3: the supervisor prepares a recipe of a finished good on a machine of the plant, with the product ×
/// machine configuration and the curing window. Each preparation is a new DRAFT version.
/// </summary>
public sealed record PrepareRecipe(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid ItemId,
    Guid MachineId,
    decimal UnitsPerBatch,
    decimal UnitsPerCycle,
    decimal UnitsPerRack,
    int MinCuringHours,
    int MaxCuringHours,
    IReadOnlyList<RecipeLineInput> Lines) : IPlantScopedCommand;

/// <summary>The plant manager approves a DRAFT (four eyes); the ACTIVE version of the product × machine becomes SUPERSEDED.</summary>
public sealed record ApproveRecipe(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid RecipeVersionId) : IPlantScopedCommand;
