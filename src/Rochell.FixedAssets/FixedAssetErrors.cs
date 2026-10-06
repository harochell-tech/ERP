namespace Rochell.FixedAssets;

public static class FixedAssetErrors
{
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string ApproverIsCreator = "APPROVER_IS_CREATOR";

    public const string ClassNotFound = "ASSET_CLASS_NOT_FOUND";
    public const string ClassInvalid = "ASSET_CLASS_INVALID";

    /// <summary>E-AF1-01-4: a card is put into service only once its category's class is approved.</summary>
    public const string ClassNotApproved = "ASSET_CLASS_NOT_APPROVED";

    public const string AssetNotFound = "FIXED_ASSET_NOT_FOUND";
    public const string AssetInvalid = "FIXED_ASSET_INVALID";

    /// <summary>E-AF1-02-4: an invoice whose cards were depreciated is not reversed; the cards are disposed of first.</summary>
    public const string AssetDepreciated = "FIXED_ASSET_DEPRECIATED";

    /// <summary>E-AF1-02-3: a settlement does not add cost to, or take it from, a disposed card.</summary>
    public const string AssetDisposed = "FIXED_ASSET_DISPOSED";

    /// <summary>E-AF1-02-4: taking a settlement's cost out would leave the card below what it already depreciated.</summary>
    public const string CostBelowDepreciation = "FIXED_ASSET_COST_BELOW_DEPRECIATION";

    /// <summary>E-AF1-02-10: an invoice line that is a fixed asset is not moved into another line's cost by a settlement.</summary>
    public const string AssetInSettlement = "FIXED_ASSET_IN_SETTLEMENT";

    public const string PlantNotFound = "PLANT_NOT_FOUND";
}
