namespace Rochell.Manufacturing;

public static class ManufacturingErrors
{
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string FieldRequired = "FIELD_REQUIRED";
    public const string FieldInvalid = "FIELD_INVALID";
    public const string FourEyes = "FOUR_EYES_REQUIRED";
    public const string LinesRequired = "LINES_REQUIRED";
    public const string DuplicateLine = "DUPLICATE_LINE";
    public const string NotFinishedGood = "NOT_FINISHED_GOOD";
    public const string NotRawMaterial = "NOT_RAW_MATERIAL";
    public const string CodeDuplicate = "CODE_DUPLICATE";
    public const string PlantMismatch = "PLANT_MISMATCH";
    public const string MachineNotActive = "MACHINE_NOT_ACTIVE";
    public const string QuantityInvalid = "QUANTITY_INVALID";
    public const string RecipeNotActive = "RECIPE_NOT_ACTIVE";
    public const string StandardCostMissing = "STANDARD_COST_MISSING";
    public const string ShiftNotActive = "SHIFT_NOT_ACTIVE";
    public const string RunExists = "RUN_EXISTS";
    public const string SummaryExists = "SUMMARY_EXISTS";
    public const string MaterialsMismatch = "MATERIALS_MISMATCH";
    public const string LocationInvalid = "LOCATION_INVALID";
    public const string UomNotConvertible = "UOM_NOT_CONVERTIBLE";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string PeriodClosed = "PERIOD_CLOSED";
    public const string CollectorSettled = "COLLECTOR_SETTLED";
    public const string LotMoved = "LOT_MOVED";
    public const string CuringNotDone = "CURING_NOT_DONE";
}
