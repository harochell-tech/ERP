namespace Rochell.Reconciliation;

public static class ReconciliationErrors
{
    public const string UnknownReconciliation = "UNKNOWN_RECONCILIATION";
    public const string UnknownComponent = "UNKNOWN_COMPONENT";
    public const string PeriodNotFound = "PERIOD_NOT_FOUND";
    public const string PeriodNotEnded = "PERIOD_NOT_ENDED";
    public const string ComponentNotOpen = "COMPONENT_NOT_OPEN";
    public const string ComponentNotClosed = "COMPONENT_NOT_CLOSED";
    public const string IntegrityNotSealed = "INTEGRITY_NOT_SEALED";
    public const string ReconciliationErrorsFound = "RECONCILIATION_ERRORS";
    public const string ReopenAlreadyRequested = "REOPEN_ALREADY_REQUESTED";
    public const string ReopenNotRequested = "REOPEN_NOT_REQUESTED";
    public const string SamePerson = "SAME_PERSON";
    public const string ReasonRequired = "REASON_REQUIRED";
}

/// <summary>Close components of VS#1 (Frozen Baseline §11.7).</summary>
public static class Components
{
    public const string InventoryMovements = "INV-MOV";
    public const string AccountsPayable = "AP-REC";

    public static bool IsKnown(string component) => component is InventoryMovements or AccountsPayable;
}
