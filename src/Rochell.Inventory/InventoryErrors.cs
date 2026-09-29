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

    /// <summary>E-VS3-02b-7: an opening balance (an inflow, v2.1 §6).</summary>
    public const string Opening = "OPENING";

    /// <summary>E-VS3-04-2: a move between two locations of a plant (patio ⇄ TRANSITO).</summary>
    public const string Transfer = "TRANSFER";

    /// <summary>E-MFG1-01-8, E-MFG1-03-9: production consumption and receipt, and their exact reversals.</summary>
    public const string ProductionIssue = "PRODUCTION_ISSUE";
    public const string ProductionReceipt = "PRODUCTION_RECEIPT";
    public const string ProductionIssueReversal = "PRODUCTION_ISSUE_REVERSAL";
    public const string ProductionReceiptReversal = "PRODUCTION_RECEIPT_REVERSAL";
}
