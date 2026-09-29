namespace Rochell.Sales;

public static class SalesErrors
{
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string FieldRequired = "FIELD_REQUIRED";
    public const string FieldInvalid = "FIELD_INVALID";
    public const string RncInvalid = "RNC_INVALID";
    public const string CustomerExists = "CUSTOMER_EXISTS";
    public const string NotCustomer = "NOT_CUSTOMER";
    public const string NotDraft = "NOT_DRAFT";
    public const string TermsRequired = "CUSTOMER_TERMS_REQUIRED";
    public const string AmountInvalid = "AMOUNT_INVALID";
    public const string FourEyes = "FOUR_EYES_REQUIRED";
    public const string NotFinishedGood = "NOT_FINISHED_GOOD";
    public const string UomNotConvertible = "UOM_NOT_CONVERTIBLE";
    public const string DuplicateLine = "DUPLICATE_LINE";
    public const string LinesRequired = "LINES_REQUIRED";
    public const string InTransitExists = "IN_TRANSIT_EXISTS";
    public const string RecipeNotActive = "RECIPE_NOT_ACTIVE";
    public const string MaterialPricesMismatch = "MATERIAL_PRICES_MISMATCH";
    public const string PlateInvalid = "PLATE_INVALID";
    public const string PlateDuplicate = "PLATE_DUPLICATE";
    public const string NationalIdInvalid = "NATIONAL_ID_INVALID";
    public const string NationalIdDuplicate = "NATIONAL_ID_DUPLICATE";
}
