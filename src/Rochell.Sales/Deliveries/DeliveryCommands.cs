using Rochell.Platform.Commands;

namespace Rochell.Sales.Deliveries;

public sealed record DeliveryPlanLine(Guid SalesOrderLineId, decimal Quantity);

/// <summary>E-VS3-04-13: a delivery CD-… of one CONFIRMED / PARTIALLY_DELIVERED order, in the order's units, never above what is still open.</summary>
public sealed record PlanDelivery(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, IReadOnlyList<DeliveryPlanLine> Lines) : ICommand;

/// <summary>E-VS3-04-6: PLANNED → LOADING with our vehicle and driver (site delivery) or the customer's plate and driver (pickup).</summary>
public sealed record StartLoading(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, long ExpectedVersion, Guid? VehicleId, Guid? DriverId, string? CustomerVehiclePlate, string? CustomerDriverName)
    : ICommand;

/// <summary>E-LAB1-03-10/11: a rack label scanned while loading — its lot, and the rack when the QR names it.</summary>
public sealed record ScannedRack(Guid LotId, int? RackNo = null);

/// <remarks><c>Scans</c> (E-LAB1-03-11/12): the racks scanned for the line, in the order scanned; the gate-out takes their lots first.</remarks>
public sealed record LoadedLine(Guid DeliveryLineId, Guid SourceLocationId, IReadOnlyList<ScannedRack>? Scans = null);

/// <summary>
/// E-VS3-04-4/5: LOADING → LOADED; each line's source location holds the quantity (in base units). A scanned lot must have stock of the
/// line's item in that location and not be blocked (E-LAB1-03-11).
/// </summary>
public sealed record ConfirmLoaded(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, long ExpectedVersion, IReadOnlyList<LoadedLine> Lines) : ICommand;

/// <summary>
/// C-08 (E-VS3-04-1…3, 7, 9…11): weighing and gate out. Lots leave FIFO; a pickup transfers control here (P-16), a site delivery
/// moves to TRANSITO (P-15) and waits for the POD.
/// </summary>
public sealed record RecordGateOut(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, long ExpectedVersion, decimal GrossKg, decimal TareKg, string WeighTicketRef, string WeighTicketSha256)
    : ICommand;

public sealed record PodLine(Guid DeliveryLineId, decimal QtyReceived, decimal QtyReturned);

/// <summary>
/// C-09 (E-VS3-04-8): the POD of a site delivery. Received quantities transfer control (P-16); returned ones go back from
/// TRANSITO to their location (P-15R); the rest is a transit loss (P-30). Any return or loss needs the exception reason.
/// </summary>
public sealed record RecordPod(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, long ExpectedVersion, string ReceivedByName, DateTime ReceivedAt, string EvidenceRef,
    string EvidenceSha256, IReadOnlyList<PodLine> Lines, string? ExceptionReason) : ICommand;

/// <summary>E-VS3-04-8: total rejection at the site — everything returns (P-15R) and the delivery is RETURNED.</summary>
public sealed record RecordReturnTrip(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-VS3-04-14: PLANNED, LOADING or LOADED → CANCELLED with a reason (nothing moved yet).</summary>
public sealed record CancelDelivery(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-VS3-04-13: Crédito closes a PARTIALLY_DELIVERED order short, with a reason, when no delivery of it is open.</summary>
public sealed record CloseShortSalesOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion, string Reason) : ICommand;
