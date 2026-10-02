using Rochell.Platform.Commands;

namespace Rochell.Sales.Fleet;

/// <summary>E-VS3-01-4/15, E-VS3-02-9: Despacho registers a vehicle (plate upper case, 5–10 letters and digits; capacity in kg).</summary>
/// <remarks>E-FLT-1, E-FLT-2: <paramref name="FleetCode"/> is the vehicle's «ficha» ("BR 09"), required; the insurance policy number is optional.</remarks>
public sealed record RegisterVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Plate, decimal CapacityKg, string? FleetCode = null, string? InsurancePolicyNo = null) : ICommand;

public sealed record UpdateVehicle(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid VehicleId, long ExpectedVersion, decimal CapacityKg, string? FleetCode = null, string? InsurancePolicyNo = null) : ICommand;

public sealed record DeactivateVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid VehicleId, long ExpectedVersion) : ICommand;

public sealed record ActivateVehicle(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid VehicleId, long ExpectedVersion) : ICommand;

/// <summary>A driver with name and cédula (11 digits, unique per company).</summary>
/// <remarks>E-FLT-3: <paramref name="LicenseExpiresOn"/> is the date the driver's licence expires; an expired one only warns (E-FLT-4).</remarks>
public sealed record RegisterDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, string FullName, string NationalId, DateOnly? LicenseExpiresOn = null) : ICommand;

public sealed record UpdateDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, long ExpectedVersion, string FullName, DateOnly? LicenseExpiresOn = null) : ICommand;

public sealed record DeactivateDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, long ExpectedVersion) : ICommand;

public sealed record ActivateDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, long ExpectedVersion) : ICommand;
