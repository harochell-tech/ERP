using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Procurement.SupplierInvoices;

/// <summary>A stored invoice line.</summary>
public sealed record SupplierInvoiceLine(Guid Id, int LineNo, string LineKind, Guid PurchaseOrderLineId, decimal Quantity, decimal UnitPrice, decimal NetAmount);

/// <summary>The evaluation of one line (E-PR13-1/2).</summary>
public sealed record LineMatch(Guid LineId, decimal QtyAvailable, decimal QtyDiff, decimal PriceDiff, decimal AmountDiff, bool QtyExceeds, bool WithinTolerance);

/// <summary>
/// E-10: per line kind, how a line is validated and matched (PR-13b adds posting). VS#1 has exactly one implementation,
/// <see cref="InventoryPoLineHandler"/>; another kind needs an errata/ADR, a migration relaxing the CHECK and its tests.
/// </summary>
public interface ISupplierInvoiceLineHandler
{
    string LineKind { get; }

    Task ValidateAsync(CommandContext context, Guid partyId, SupplierInvoiceLineInput line, CancellationToken cancellationToken);

    Task<LineMatch> MatchAsync(CommandContext context, SupplierInvoiceLine line, ResolvedPolicy purchasingPolicy, CancellationToken cancellationToken);
}

public static class SupplierInvoiceLineHandlers
{
    private static readonly ISupplierInvoiceLineHandler[] All = [new InventoryPoLineHandler()];

    public static ISupplierInvoiceLineHandler For(string lineKind)
        => All.SingleOrDefault(h => h.LineKind == lineKind)
            ?? throw new DomainException(ProcurementErrors.LineKindNotSupported, $"Invoice lines of kind '{lineKind}' are not supported in VS#1; every line must bill an inventory PO line (E-10).");
}

/// <summary>Inventory PO → goods receipt → invoice line.</summary>
public sealed class InventoryPoLineHandler : ISupplierInvoiceLineHandler
{
    public string LineKind => SupplierInvoiceLineKinds.InventoryPo;

    public async Task ValidateAsync(CommandContext context, Guid partyId, SupplierInvoiceLineInput line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(line);
        if (line.Quantity <= 0 || decimal.Round(line.Quantity, 6) != line.Quantity)
        {
            throw new DomainException(ProcurementErrors.QuantityInvalid, "Invoiced quantities must be positive with at most 6 decimals.");
        }

        if (line.UnitPrice <= 0 || decimal.Round(line.UnitPrice, 6) != line.UnitPrice)
        {
            throw new DomainException(ProcurementErrors.PriceInvalid, "Invoice prices must be positive with at most 6 decimals.");
        }

        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT po.party_id = @party AND po.status IN ('APPROVED', 'PARTIALLY_RECEIVED', 'RECEIVED')
            FROM pur.purchase_order_line pol JOIN pur.purchase_order po ON po.po_id = pol.po_id
            WHERE pol.company_id = @c AND pol.po_line_id = @l
            """,
            ("c", context.CompanyId),
            ("l", line.PurchaseOrderLineId),
            ("party", partyId));
        switch (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
        {
            case null:
                throw new DomainException(ProcurementErrors.LineNotFound, "An invoice line references a PO line that does not exist.");
            case false:
                throw new DomainException(ProcurementErrors.PoLineNotInvoiceable, "An invoice line references a PO line of another supplier or of an order that is not approved.");
        }
    }

    /// <summary>
    /// available = received − invoiced; quantity above it is an exception that is never approvable (E-PR13-1).
    /// Price within tolerance when |P′ − P| ≤ P × match_price_tolerance_pct OR |Q × (P′ − P)| ≤ match_amount_tolerance_abs (E-PR13-2).
    /// </summary>
    public async Task<LineMatch> MatchAsync(CommandContext context, SupplierInvoiceLine line, ResolvedPolicy purchasingPolicy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(purchasingPolicy);
        decimal received, invoiced, poPrice;
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT qty_received, qty_invoiced, unit_price FROM pur.purchase_order_line WHERE po_line_id = @l",
            ("l", line.PurchaseOrderLineId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            (received, invoiced, poPrice) = (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
        }

        var available = received - invoiced;
        var qtyDiff = line.Quantity - available;
        var priceDiff = line.UnitPrice - poPrice;
        var amountDiff = decimal.Round(line.Quantity * priceDiff, 2, MidpointRounding.AwayFromZero);
        var qtyExceeds = qtyDiff > 0;
        var priceWithin = Math.Abs(priceDiff) <= poPrice * purchasingPolicy.Decimal(PolicyParameters.MatchPriceTolerancePct)
            || Math.Abs(amountDiff) <= purchasingPolicy.Decimal(PolicyParameters.MatchAmountToleranceAbs);
        return new LineMatch(line.Id, available, qtyDiff, priceDiff, amountDiff, qtyExceeds, !qtyExceeds && priceWithin);
    }
}
