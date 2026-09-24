namespace Rochell.Audit;

/// <summary>
/// E-PR15-2: the hash chains of VS#1, one per company × ledger, and what a group (the unit of sealing) is in each.
/// GL: a journal (header, then entries by line_no). INV_QTY / INV_VALUE: the rows of one source event. DOMAIN_EVENT: the events
/// of one command, by command_event_index.
/// </summary>
public static class Chains
{
    public const string Gl = "GL";
    public const string InventoryQuantity = "INV_QTY";
    public const string InventoryValue = "INV_VALUE";
    public const string DomainEvent = "DOMAIN_EVENT";

    public static IReadOnlyList<string> All { get; } = [Gl, InventoryQuantity, InventoryValue, DomainEvent];
}
