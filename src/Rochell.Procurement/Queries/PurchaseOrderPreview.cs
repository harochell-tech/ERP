using Rochell.Platform.Commands;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Tax;

namespace Rochell.Procurement.Queries;

/// <summary>
/// E-UX4-3: what a draft purchase order would come to — the lines are validated exactly as CreatePurchaseOrder validates them, then
/// net per line (quantity × unit price, 2 decimals), the net total and the ITBIS the purchase rules in force on the order date would
/// determine. A query: nothing is written. Needs <c>purchase_order:create</c>, like the form it serves, in the order's plant.
/// </summary>
public sealed record PreviewPurchaseOrder(
    Guid CompanyId, Guid SessionId, Guid? PlantId, Guid PartyId, DateOnly OrderDate, IReadOnlyList<PurchaseOrderLineInput> Lines) : IPlantScopedQuery;

/// <summary>The HTTP body of the preview: the create form's fields.</summary>
public sealed record PurchaseOrderPreviewRequest(Guid PlantId, Guid PartyId, DateOnly OrderDate, IReadOnlyList<PurchaseOrderLineInput> Lines);

public sealed record PurchaseOrderPreviewLine(int LineNo, Guid ItemId, string Uom, decimal Quantity, decimal UnitPrice, decimal NetAmount, decimal? Itbis);

/// <summary>
/// <see cref="ItbisTotal"/> and <see cref="Total"/> (net + ITBIS) are null when no purchase ITBIS rule is in force on the date;
/// <see cref="ItbisUnavailableCode"/> (FISCAL_GATE_CLOSED) and <see cref="ItbisUnavailableReason"/> then say why.
/// </summary>
public sealed record PurchaseOrderPreview(
    IReadOnlyList<PurchaseOrderPreviewLine> Lines, decimal NetTotal, decimal? ItbisTotal, decimal? Total, string? ItbisUnavailableCode, string? ItbisUnavailableReason);

[RequiresPermission("purchase_order:create")]
public sealed class PreviewPurchaseOrderHandler : IQueryHandler<PreviewPurchaseOrder>
{
    public string QueryType => "Procurement.PreviewPurchaseOrder";

    public async Task<string> HandleAsync(PreviewPurchaseOrder query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var lines = query.Lines ?? [];
        await PurchaseOrderStore.ValidateAsync(context.Connection, context.Transaction, context.CompanyId, query.PartyId, query.OrderDate, lines, cancellationToken).ConfigureAwait(false);

        // Synthetic line ids tie each line to its ITBIS; the order has none yet.
        var priced = lines.Select((l, i) => (Id: new Guid(i + 1, 0, 0, new byte[8]), No: i + 1, Line: l, Net: decimal.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero))).ToList();
        var itbis = await TaxEngine.EstimateItbisAsync(
            context.Connection, context.Transaction, context.CompanyId, query.OrderDate, sale: false, [.. priced.Select(p => new TaxLineInput(p.Id, p.Line.ItemId, p.Net))], cancellationToken).ConfigureAwait(false);
        var net = Money.Zero + priced.Sum(p => p.Net);
        return ApiJson.Serialize(new PurchaseOrderPreview(
            [.. priced.Select(p => new PurchaseOrderPreviewLine(p.No, p.Line.ItemId, p.Line.Uom, p.Line.Quantity, p.Line.UnitPrice, p.Net, itbis.ByLine?[p.Id]))],
            net,
            itbis.Total,
            net + itbis.Total,
            itbis.UnavailableCode,
            itbis.UnavailableReason));
    }
}
