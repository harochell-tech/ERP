namespace Rochell.Procurement;

public static class ProcurementErrors
{
    public const string NotFound = "PURCHASE_ORDER_NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string PlantMismatch = "PLANT_MISMATCH";
    public const string SupplierNotActive = "SUPPLIER_NOT_ACTIVE";
    public const string ItemNotActive = "ITEM_NOT_ACTIVE";
    public const string UomNotConvertible = "UOM_NOT_CONVERTIBLE";
    public const string LinesRequired = "LINES_REQUIRED";
    public const string QuantityInvalid = "QUANTITY_INVALID";
    public const string PriceInvalid = "PRICE_INVALID";
    public const string ApproverIsCreator = "APPROVER_IS_CREATOR";
    public const string ApprovalLimitExceeded = "APPROVAL_LIMIT_EXCEEDED";
    public const string AlreadyReceived = "ALREADY_RECEIVED";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string LineNotFound = "LINE_NOT_FOUND";
}

public static class PurchaseOrderStatus
{
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Approved = "APPROVED";
    public const string PartiallyReceived = "PARTIALLY_RECEIVED";
    public const string Received = "RECEIVED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";
}
