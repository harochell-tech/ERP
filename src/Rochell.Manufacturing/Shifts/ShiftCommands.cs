using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Shifts;

/// <summary>
/// E-MFG1-01-4, E-MFG1-02-2: a shift of a plant; <c>EndsAt</c> earlier than <c>StartsAt</c> is a night shift that ends the next day
/// (its business date is the start date, E-MFG1-15).
/// </summary>
public sealed record DefineShift(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, string Code, TimeOnly StartsAt, TimeOnly EndsAt) : IPlantScopedCommand;

public sealed record UpdateShiftTimes(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid ShiftId, long ExpectedVersion, TimeOnly StartsAt, TimeOnly EndsAt) : IPlantScopedCommand;

/// <summary><c>Status</c>: ACTIVE or INACTIVE.</summary>
public sealed record SetShiftStatus(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid ShiftId, long ExpectedVersion, string Status) : IPlantScopedCommand;
