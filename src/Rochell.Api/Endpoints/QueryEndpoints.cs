using System.Text.Json;
using Rochell.Api.Http;
using Rochell.Finance.Configuration;
using Rochell.Finance.Explain;
using Rochell.Finance.Policies;
using Rochell.MasterData.Queries;
using Rochell.Platform.Queries;
using Rochell.Procurement.Queries;
using Rochell.Reconciliation.Queries;
using Rochell.Tax;
using Rochell.Treasury.Statements;

namespace Rochell.Api.Endpoints;

/// <summary>
/// E-PR18-4 read side: <c>GET /api/v1/companies/{companyId}/{module}/{resource}</c>, each on the query pipeline (E-PR17-1).
/// A plant-scoped reader passes <c>plantId</c>; lists page with <c>limit</c> (1…200, default 50) and <c>offset</c>.
/// </summary>
public static class QueryEndpoints
{
    private const int DefaultLimit = 50;

    public static void AddQueryHandlers(this IServiceCollection services)
    {
        foreach (var handler in Handlers)
        {
            services.AddTransient(handler);
        }
    }

    public static IReadOnlyList<Type> Handlers { get; } =
    [
        typeof(ListSuppliersHandler), typeof(ListItemsHandler), typeof(ListPlantsHandler),
        typeof(ListPurchaseOrdersHandler), typeof(GetPurchaseOrderHandler), typeof(ListGoodsReceiptsHandler), typeof(GetGoodsReceiptHandler),
        typeof(ListReceiptCorrectionsHandler), typeof(ListSupplierInvoicesHandler), typeof(GetSupplierInvoiceHandler),
        typeof(ListPeriodsHandler), typeof(ListReconciliationRunsHandler), typeof(GetReconciliationRunHandler),
        typeof(ListEventJournalsHandler), typeof(ExplainEntryHandler),
        typeof(ListAccountsHandler), typeof(ListAccountRoleMapsHandler), typeof(ListPostingRulesHandler), typeof(ListAccountingPoliciesHandler),
        typeof(ListFiscalSourcesHandler), typeof(ListFiscalRulesHandler), typeof(SuggestBankMatchesHandler),
    ];

    public static void MapQueryEndpoints(this RouteGroupBuilder company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var masterData = company.MapGroup("/master-data").WithTags("MasterData");
        masterData.MapGet("/suppliers", (HttpContext http, Guid companyId, Guid? plantId, string? status, int? limit, int? offset, ListSuppliersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSuppliers(companyId, s, plantId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<SupplierList>(nameof(ListSuppliers));
        masterData.MapGet("/items", (HttpContext http, Guid companyId, Guid? plantId, string? status, int? limit, int? offset, ListItemsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListItems(companyId, s, plantId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ItemList>(nameof(ListItems));
        masterData.MapGet("/plants", (HttpContext http, Guid companyId, Guid? plantId, ListPlantsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPlants(companyId, s, plantId), handler, ct))
            .Describe<PlantList>(nameof(ListPlants));

        var procurement = company.MapGroup("/procurement").WithTags("Procurement");
        procurement.MapGet("/purchase-orders", (HttpContext http, Guid companyId, Guid? plantId, string? status, Guid? supplierId, int? limit, int? offset, ListPurchaseOrdersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPurchaseOrders(companyId, s, plantId, status, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<PurchaseOrderList>(nameof(ListPurchaseOrders));
        procurement.MapGet("/purchase-orders/{purchaseOrderId:guid}", (HttpContext http, Guid companyId, Guid purchaseOrderId, Guid? plantId, GetPurchaseOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPurchaseOrder(companyId, s, purchaseOrderId, plantId), handler, ct))
            .Describe<PurchaseOrderDetail>(nameof(GetPurchaseOrder), notFound: true);
        procurement.MapGet("/goods-receipts", (HttpContext http, Guid companyId, Guid? plantId, Guid? purchaseOrderId, string? documentStatus, int? limit, int? offset, ListGoodsReceiptsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListGoodsReceipts(companyId, s, plantId, purchaseOrderId, documentStatus, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<GoodsReceiptList>(nameof(ListGoodsReceipts));
        procurement.MapGet("/goods-receipts/{goodsReceiptId:guid}", (HttpContext http, Guid companyId, Guid goodsReceiptId, Guid? plantId, GetGoodsReceiptHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetGoodsReceipt(companyId, s, goodsReceiptId, plantId), handler, ct))
            .Describe<GoodsReceiptDetail>(nameof(GetGoodsReceipt), notFound: true);
        procurement.MapGet("/receipt-corrections", (HttpContext http, Guid companyId, Guid? plantId, string? documentStatus, int? limit, int? offset, ListReceiptCorrectionsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReceiptCorrections(companyId, s, plantId, documentStatus, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ReceiptCorrectionList>(nameof(ListReceiptCorrections));
        procurement.MapGet("/supplier-invoices", (HttpContext http, Guid companyId, string? documentStatus, string? accountingStatus, Guid? supplierId, int? limit, int? offset, ListSupplierInvoicesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSupplierInvoices(companyId, s, documentStatus, accountingStatus, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<SupplierInvoiceList>(nameof(ListSupplierInvoices));
        procurement.MapGet("/supplier-invoices/{supplierInvoiceId:guid}", (HttpContext http, Guid companyId, Guid supplierInvoiceId, GetSupplierInvoiceHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetSupplierInvoice(companyId, s, supplierInvoiceId), handler, ct))
            .Describe<SupplierInvoiceDetail>(nameof(GetSupplierInvoice), notFound: true);

        var reconciliation = company.MapGroup("/reconciliation").WithTags("Reconciliation");
        reconciliation.MapGet("/periods", (HttpContext http, Guid companyId, int year, ListPeriodsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPeriods(companyId, s, year), handler, ct))
            .Describe<PeriodList>(nameof(ListPeriods));
        reconciliation.MapGet("/runs", (HttpContext http, Guid companyId, string? reconCode, int? limit, int? offset, ListReconciliationRunsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReconciliationRuns(companyId, s, reconCode, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ReconciliationRunList>(nameof(ListReconciliationRuns));
        reconciliation.MapGet("/runs/{runId:guid}", (HttpContext http, Guid companyId, Guid runId, GetReconciliationRunHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetReconciliationRun(companyId, s, runId), handler, ct))
            .Describe<ReconciliationRunDetail>(nameof(GetReconciliationRun), notFound: true);

        var finance = company.MapGroup("/finance").WithTags("Finance");
        finance.MapGet("/events/{sourceEventId:guid}/journals", (HttpContext http, Guid companyId, Guid sourceEventId, ListEventJournalsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListEventJournals(companyId, s, sourceEventId), handler, ct))
            .Describe<EventJournals>(nameof(ListEventJournals), notFound: true);

        // E-B03-15-1: configuration lists for the approval screens (configuration:read).
        finance.MapGet("/accounts", (HttpContext http, Guid companyId, ListAccountsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAccounts(companyId, s), handler, ct))
            .Describe<AccountList>(nameof(ListAccounts));
        finance.MapGet("/account-role-maps", (HttpContext http, Guid companyId, string? status, ListAccountRoleMapsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAccountRoleMaps(companyId, s, status), handler, ct))
            .Describe<AccountRoleMapList>(nameof(ListAccountRoleMaps));
        finance.MapGet("/posting-rules", (HttpContext http, Guid companyId, ListPostingRulesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPostingRules(companyId, s), handler, ct))
            .Describe<PostingRuleList>(nameof(ListPostingRules));
        finance.MapGet("/accounting-policies", (HttpContext http, Guid companyId, ListAccountingPoliciesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAccountingPolicies(companyId, s), handler, ct))
            .Describe<AccountingPolicyList>(nameof(ListAccountingPolicies));

        var tax = company.MapGroup("/tax").WithTags("Tax");
        tax.MapGet("/fiscal-sources", (HttpContext http, Guid companyId, ListFiscalSourcesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListFiscalSources(companyId, s), handler, ct))
            .Describe<FiscalSourceList>(nameof(ListFiscalSources));
        tax.MapGet("/fiscal-rules", (HttpContext http, Guid companyId, ListFiscalRulesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListFiscalRules(companyId, s), handler, ct))
            .Describe<FiscalRuleList>(nameof(ListFiscalRules));

        // E-VS2-05-6: match suggestions for a statement's unmatched DEBIT lines (the other treasury queries come with VS2-07).
        var treasury = company.MapGroup("/treasury").WithTags("Treasury");
        treasury.MapGet("/bank-statements/{statementId:guid}/match-suggestions", (HttpContext http, Guid companyId, Guid statementId, SuggestBankMatchesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new SuggestBankMatches(companyId, s, statementId), handler, ct))
            .Describe<MatchSuggestions>(nameof(SuggestBankMatches), notFound: true);

        // E-PR17-6: the HTTP endpoint of "Explain this entry". Its result is the PR-17 document, returned as is.
        finance.MapGet("/entries/{glEntryId:guid}/explanation", (HttpContext http, Guid companyId, Guid glEntryId, ExplainEntryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ExplainEntry(companyId, s, glEntryId), handler, ct))
            .Describe<JsonElement>(nameof(ExplainEntry), notFound: true);
    }

    private static RouteHandlerBuilder Describe<TResult>(this RouteHandlerBuilder endpoint, string name, bool notFound = false)
    {
        endpoint
            .WithName(name)
            .Produces<TResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithMetadata(new QueryEndpointMetadata(name));
        return notFound ? endpoint.ProducesProblem(StatusCodes.Status404NotFound) : endpoint;
    }
}

/// <summary>Marks a query endpoint (tests check every query handler is exposed).</summary>
public sealed record QueryEndpointMetadata(string QueryName);
