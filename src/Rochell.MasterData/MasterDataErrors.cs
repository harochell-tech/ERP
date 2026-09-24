namespace Rochell.MasterData;

public static class MasterDataErrors
{
    public const string RncInvalid = "RNC_INVALID";
    public const string RncDuplicate = "RNC_DUPLICATE";
    public const string NotFound = "NOT_FOUND";
    public const string NotDraft = "NOT_DRAFT";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string ItemCodeInvalid = "ITEM_CODE_INVALID";
    public const string ItemCodeDuplicate = "ITEM_CODE_DUPLICATE";
    public const string CategoryInvalid = "ITEM_CATEGORY_INVALID";
    public const string UomUnknown = "UOM_UNKNOWN";
    public const string ConversionInvalid = "CONVERSION_INVALID";
    public const string ConversionRetroactive = "CONVERSION_RETROACTIVE";
    public const string FieldRequired = "FIELD_REQUIRED";
}

public static class ItemCategories
{
    /// <summary>E-PR04-7: closed list (also enforced by a CHECK constraint).</summary>
    public static IReadOnlyList<string> All { get; } = ["CEMENTO", "AGREGADO", "ADITIVO", "OTRA_MATERIA_PRIMA"];
}
