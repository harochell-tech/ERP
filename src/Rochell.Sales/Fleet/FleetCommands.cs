using Rochell.Platform.Commands;

namespace Rochell.Sales.Fleet;

/// <summary>E-VS3-01-4/15, E-VS3-02-9: Despacho registers a vehicle (plate upper case, 5–10 letters and digits; capacity in kg).</summary>
public sealed record RegisterVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Plate, decimal CapacityKg) : ICommand;

public sealed record UpdateVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid VehicleId, long ExpectedVersion, decimal CapacityKg) : ICommand;

public sealed record DeactivateVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid VehicleId, long ExpectedVersion) : ICommand;

public sealed record ActivateVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid VehicleId, long ExpectedVersion) : ICommand;

/// <summary>A driver with name and cédula (11 digits, unique per company).</summary>
public sealed record RegisterDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, string FullName, string NationalId) : ICommand;

public sealed record UpdateDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, long ExpectedVersion, string FullName) : ICommand;

public sealed record DeactivateDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, long ExpectedVersion) : ICommand;

public sealed record ActivateDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, long ExpectedVersion) : ICommand;
