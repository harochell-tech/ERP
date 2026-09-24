namespace Rochell.Inventory;

public static class InventoryErrors
{
    /// <summary>CON-03: the conditional UPDATE found less stock than requested.</summary>
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string LocationNotFound = "LOCATION_NOT_FOUND";
}

public static class MovementTypes
{
    public const string Receipt = "RECEIPT";
    public const string ReceiptReversal = "RECEIPT_REVERSAL";
    public const string ReceiptCorrection = "RECEIPT_CORRECTION";
    public const string Issue = "ISSUE";
    public const string ValuationAdjustment = "VALUATION_ADJUSTMENT";
    public const string ValuationReallocation = "VALUATION_REALLOCATION";
    public const string PriceAdjustment = "PRICE_ADJUSTMENT";
    public const string Repost = "REPOST";
    public const string ResidualAdjustment = "RESIDUAL_ADJUSTMENT";
}
