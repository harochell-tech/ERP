namespace Rochell.MasterData;

public static class MasterDataErrors
{
    public const string RncInvalid = "RNC_INVALID";
    public const string RncDuplicate = "RNC_DUPLICATE";

    /// <summary>E-USD1-03-9: the foreign supplier's country is a two-letter code.</summary>
    public const string CountryInvalid = "COUNTRY_INVALID";

    /// <summary>E-USD1-03-9: one supplier per country and foreign tax id.</summary>
    public const string ForeignTaxIdDuplicate = "FOREIGN_TAX_ID_DUPLICATE";

    /// <summary>E-USD1-03-9: a local supplier is corrected with UpdateSupplier, a foreign one with UpdateForeignSupplierDraft.</summary>
    public const string SupplierKindMismatch = "SUPPLIER_KIND_MISMATCH";
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
    public const string FieldInvalid = "FIELD_INVALID";
    public const string ImportFileInvalid = "IMPORT_FILE_INVALID";
    public const string ImportFileTooLarge = "IMPORT_FILE_TOO_LARGE";
    public const string BatchInvalid = "BATCH_INVALID";
}

public static class ItemCategories
{
    /// <summary>E-PR04-7: closed list (also enforced by a CHECK constraint).</summary>
    public static IReadOnlyList<string> All { get; } = ["CEMENTO", "AGREGADO", "ADITIVO", "OTRA_MATERIA_PRIMA"];

    /// <summary>E-VS3-01-2: categories of finished goods.</summary>
    public static IReadOnlyList<string> FinishedGoods { get; } = ["BLOQUE", "ADOQUIN", "OTRO_PT"];

    /// <summary>E-SRV1-8: the freight service.</summary>
    public static IReadOnlyList<string> Services { get; } = ["TRANSPORTE"];
}
