using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Machines;

/// <summary>E-MFG1-01-3, E-MFG1-02-2: the Gerente de planta registers a machine of a plant (code unique per company, upper case).</summary>
public sealed record CreateMachine(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, string Code, string Name) : IPlantScopedCommand;

public sealed record RenameMachine(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid MachineId, long ExpectedVersion, string Name) : IPlantScopedCommand;

/// <summary><c>Status</c>: ACTIVE or INACTIVE.</summary>
public sealed record SetMachineStatus(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid MachineId, long ExpectedVersion, string Status) : IPlantScopedCommand;
