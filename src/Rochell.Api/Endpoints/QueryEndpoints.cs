using System.Globalization;
using System.Text.Json;
using Rochell.Api.Http;
using Rochell.Audit;
using Rochell.FixedAssets.Depreciation;
using Rochell.FixedAssets.Disposals;
using Rochell.FixedAssets.Queries;
using Rochell.Finance.Configuration;
using Rochell.Finance.Explain;
using Rochell.Finance.ExchangeRates;
using Rochell.Finance.Ledger;
using Rochell.Finance.Policies;
using Rochell.Identity.Queries;
using Rochell.Manufacturing.Queries;
using Rochell.MasterData.Company;
using Rochell.MasterData.Import;
using Rochell.MasterData.Suppliers;
using Rochell.MasterData.Queries;
using Rochell.Platform.Queries;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.Imports;
using Rochell.Procurement.Queries;
using Rochell.Reconciliation.Queries;
using Rochell.Sales.Customers;
using Rochell.Sales.Mail;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;
using Rochell.Sales.Refunds;
using Rochell.Sales.Zones;
using Rochell.Tax;
using Rochell.Tax.Authorizations;
using Rochell.Tax.Reports;
using Rochell.Treasury.Queries;
using Rochell.Treasury.Statements;
using Rochell.Treasury.Transfers;

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
        typeof(ListSuppliersHandler), typeof(ListItemsHandler), typeof(ListPlantsHandler), typeof(ListUomsHandler), typeof(GetCompanyHandler), typeof(GetRncHandler), typeof(GetRncRegistryStatusHandler),
        typeof(PreviewSupplierImportHandler), typeof(PreviewCustomerImportHandler),
        typeof(PreviewPurchaseOrderHandler), typeof(PreviewSalesOrderHandler), typeof(PreviewCashSaleHandler), typeof(PreviewQuoteHandler), typeof(GetCreditPreviewHandler), typeof(SuggestReceiptApplicationHandler),
        typeof(ListLatestReconciliationRunsHandler), typeof(SearchJournalsHandler), typeof(GetIntegrityStatusHandler),
        typeof(ListPurchaseOrdersHandler), typeof(GetPurchaseOrderHandler), typeof(ListPurchaseOrdersToReceiveHandler), typeof(ListGoodsReceiptsHandler), typeof(GetGoodsReceiptHandler),
        typeof(ListReceiptCorrectionsHandler), typeof(ListCustomsDeclarationsHandler), typeof(ListImportSettlementsHandler), typeof(GetImportSettlementHandler), typeof(ListSupplierInvoicesHandler), typeof(ListExpenseCategoriesHandler), typeof(PreviewExpensePurchaseOrderHandler), typeof(PreviewExpenseInvoiceHandler), typeof(GetSupplierInvoiceHandler),
        typeof(ListPeriodsHandler), typeof(GetSetupStatusHandler), typeof(ListReconciliationRunsHandler), typeof(GetReconciliationRunHandler),
        typeof(GetCloseReadinessHandler), typeof(ListReconciliationDefinitionsHandler),
        typeof(ListEventJournalsHandler), typeof(ExplainEntryHandler),
        typeof(ListAssetClassesHandler), typeof(ListFixedAssetsHandler), typeof(GetFixedAssetHandler), typeof(ListDepreciationRunsHandler), typeof(ListAssetDisposalsHandler),
        typeof(ListExchangeRatesHandler), typeof(ListFxRevaluationsHandler), typeof(GetExchangeRateForDateHandler), typeof(ListAccountsHandler), typeof(ListAccountRolesHandler), typeof(ListAccountRoleMapsHandler), typeof(ListPostingRulesHandler), typeof(ListAccountingPoliciesHandler),
        typeof(ListManualJournalsHandler), typeof(GetManualJournalHandler), typeof(GetTrialBalanceHandler), typeof(GetAccountLedgerHandler),
        typeof(GetBalanceSheetHandler), typeof(GetIncomeStatementHandler), typeof(ListReportStructuresHandler), typeof(GetReportStructureHandler),
        typeof(ListFiscalSourcesHandler), typeof(ListFiscalRulesHandler), typeof(ListPurchaseTaxTypesHandler), typeof(ListFiscalAuthorizationsHandler), typeof(GetFiscalAuthorizationHandler), typeof(GetSalesOrderProformaHandler), typeof(GetReport606Handler), typeof(GetIt1SummaryHandler), typeof(GetIr17SummaryHandler), typeof(SuggestBankMatchesHandler), typeof(ListReceiptCandidatesHandler),
        typeof(GetApAgingHandler), typeof(ListBankTransfersHandler), typeof(GetPaymentProposalHandler), typeof(ListPaymentsHandler), typeof(GetPaymentHandler), typeof(ListBankAccountsHandler), typeof(ListRefundsToMatchHandler),
        typeof(ListPartyBankAccountsHandler), typeof(ListBankStatementsHandler), typeof(ListBankStatementLinesHandler), typeof(GetBankReconciliationHandler),
        typeof(ListCustomersHandler), typeof(GetCustomerHandler), typeof(ListCustomerTermsHandler), typeof(ListStandardCostsHandler), typeof(ListPriceListsHandler), typeof(ListPriceListHeadersHandler), typeof(ListDeliveryZonesHandler),
        typeof(GetPriceListHandler), typeof(ListVehiclesHandler), typeof(ListDriversHandler), typeof(ListMachinesHandler), typeof(ListShiftsHandler), typeof(ListRecipesHandler), typeof(GetRecipeHandler), typeof(ListProductionRunsHandler), typeof(GetProductionRunHandler), typeof(ListFgLotsHandler), typeof(ListCostCollectorsHandler), typeof(GetProductionDayHandler), typeof(ListOpeningBatchesHandler), typeof(GetOpeningBatchHandler),
        typeof(ListProformasHandler), typeof(GetProformaHandler), typeof(ListCustomerRefundsHandler), typeof(GetCustomerRefundHandler), typeof(ListDocumentMailHandler), typeof(GetDocumentMailPdfHandler),
        typeof(ListSalesOrdersHandler), typeof(GetSalesOrderHandler), typeof(GetCashSaleSetupHandler), typeof(ListQuotesHandler), typeof(GetQuoteHandler), typeof(GetQuotePrintHandler), typeof(GetCustomerExposureHandler), typeof(ListDeliveriesHandler), typeof(GetDeliveryHandler), typeof(GetDeliveryPrintHandler),
        typeof(ListInvoicesHandler), typeof(GetInvoiceHandler), typeof(GetInvoiceFiscalPackageHandler), typeof(ListBillableDeliveriesHandler),
        typeof(ListCreditNotesHandler), typeof(GetCreditNoteHandler), typeof(GetCreditNoteFiscalPackageHandler),
        typeof(ListReceiptsHandler), typeof(GetReceiptHandler), typeof(ListDepositsHandler), typeof(GetDepositHandler), typeof(GetArAgingHandler), typeof(GetCustomerStatementHandler), typeof(ListSalesPlantsHandler), typeof(ListSalesBankAccountsHandler),
        typeof(ListUsersHandler), typeof(ListRolesHandler), typeof(ListRoleRequestsHandler), typeof(ListLedgerDigestsHandler),
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
        masterData.MapGet("/company", (HttpContext http, Guid companyId, GetCompanyHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCompany(companyId, s), handler, ct))
            .Describe<CompanyView>(nameof(GetCompany));
        masterData.MapGet("/plants", (HttpContext http, Guid companyId, Guid? plantId, ListPlantsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPlants(companyId, s, plantId), handler, ct))
            .Describe<PlantList>(nameof(ListPlants));
        // E-UX3-11: the units of measure.
        masterData.MapGet("/uoms", (HttpContext http, Guid companyId, Guid? plantId, ListUomsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListUoms(companyId, s, plantId), handler, ct))
            .Describe<UomList>(nameof(ListUoms));
        // E-RNC-4/7: the DGII registry.
        masterData.MapGet("/rnc/{rnc}", (HttpContext http, Guid companyId, string rnc, GetRncHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetRnc(companyId, s, rnc), handler, ct))
            .Describe<RncLookup>(nameof(GetRnc));
        masterData.MapGet("/rnc-registry", (HttpContext http, Guid companyId, GetRncRegistryStatusHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetRncRegistryStatus(companyId, s), handler, ct))
            .Describe<RncRegistryStatus>(nameof(GetRncRegistryStatus));
        // E-IMP-1: what the supplier file would load (POST: the file travels in the body; read-only).
        masterData.MapPost("/suppliers/import-preview", (HttpContext http, Guid companyId, PreviewSupplierImportHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<PartyImportRequest, PreviewSupplierImport>(http, (s, b) => new PreviewSupplierImport(companyId, s, b.FileName, b.ContentBase64), handler, ct))
            .Describe<PartyImportPreview>(nameof(PreviewSupplierImport))
            .Accepts<PartyImportRequest>("application/json");

        var procurement = company.MapGroup("/procurement").WithTags("Procurement");
        procurement.MapGet("/purchase-orders", (HttpContext http, Guid companyId, Guid? plantId, string? status, Guid? supplierId, int? limit, int? offset, ListPurchaseOrdersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPurchaseOrders(companyId, s, plantId, status, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<PurchaseOrderList>(nameof(ListPurchaseOrders));
        procurement.MapGet("/purchase-orders/{purchaseOrderId:guid}", (HttpContext http, Guid companyId, Guid purchaseOrderId, Guid? plantId, GetPurchaseOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPurchaseOrder(companyId, s, purchaseOrderId, plantId), handler, ct))
            .Describe<PurchaseOrderDetail>(nameof(GetPurchaseOrder), notFound: true);
        // E-UX3-5: the orders to receive against, with open and receivable quantities.
        procurement.MapGet("/purchase-orders/to-receive", (HttpContext http, Guid companyId, Guid? plantId, Guid? supplierId, int? limit, int? offset, ListPurchaseOrdersToReceiveHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPurchaseOrdersToReceive(companyId, s, plantId, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<PurchaseOrderToReceiveList>(nameof(ListPurchaseOrdersToReceive));
        // E-UX4-3: the preview of a draft order (POST: the lines travel in the body; read-only).
        procurement.MapPost("/purchase-orders/preview", (HttpContext http, Guid companyId, PreviewPurchaseOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<PurchaseOrderPreviewRequest, PreviewPurchaseOrder>(
                    http, (s, b) => new PreviewPurchaseOrder(companyId, s, b.PlantId, b.PartyId, b.OrderDate, b.Lines), handler, ct))
            .Describe<PurchaseOrderPreview>(nameof(PreviewPurchaseOrder))
            .Accepts<PurchaseOrderPreviewRequest>("application/json");
        procurement.MapGet("/goods-receipts", (HttpContext http, Guid companyId, Guid? plantId, Guid? purchaseOrderId, string? documentStatus, int? limit, int? offset, ListGoodsReceiptsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListGoodsReceipts(companyId, s, plantId, purchaseOrderId, documentStatus, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<GoodsReceiptList>(nameof(ListGoodsReceipts));
        procurement.MapGet("/goods-receipts/{goodsReceiptId:guid}", (HttpContext http, Guid companyId, Guid goodsReceiptId, Guid? plantId, GetGoodsReceiptHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetGoodsReceipt(companyId, s, goodsReceiptId, plantId), handler, ct))
            .Describe<GoodsReceiptDetail>(nameof(GetGoodsReceipt), notFound: true);
        procurement.MapGet("/receipt-corrections", (HttpContext http, Guid companyId, Guid? plantId, string? documentStatus, int? limit, int? offset, ListReceiptCorrectionsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReceiptCorrections(companyId, s, plantId, documentStatus, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ReceiptCorrectionList>(nameof(ListReceiptCorrections));
        // E-GAS-05-6: a draft expense order priced with the taxes of its lines' types (POST: the lines travel in the body; read-only).
        procurement.MapPost("/expense-purchase-orders/preview", (HttpContext http, Guid companyId, PreviewExpensePurchaseOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<ExpenseOrderPreviewRequest, PreviewExpensePurchaseOrder>(http, (s, b) => new PreviewExpensePurchaseOrder(companyId, s, b.OrderDate, b.Lines, b.Currency ?? "DOP"), handler, ct))
            .Describe<ExpenseOrderPreview>(nameof(PreviewExpensePurchaseOrder))
            .Accepts<ExpenseOrderPreviewRequest>("application/json");
        procurement.MapPost("/expense-invoices/preview", (HttpContext http, Guid companyId, PreviewExpenseInvoiceHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<ExpenseOrderPreviewRequest, PreviewExpenseInvoice>(http, (s, b) => new PreviewExpenseInvoice(companyId, s, b.OrderDate, b.Lines, b.Currency ?? "DOP"), handler, ct))
            .Describe<ExpenseOrderPreview>(nameof(PreviewExpenseInvoice))
            .Accepts<ExpenseOrderPreviewRequest>("application/json");
        // E-GAS-03-2: expense categories (master data).
        procurement.MapGet("/expense-categories", (HttpContext http, Guid companyId, string? status, ListExpenseCategoriesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListExpenseCategories(companyId, s, status), handler, ct))
            .Describe<ExpenseCategoryList>(nameof(ListExpenseCategories));
        procurement.MapGet("/supplier-invoices", (HttpContext http, Guid companyId, string? documentStatus, string? accountingStatus, Guid? supplierId, int? limit, int? offset, ListSupplierInvoicesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSupplierInvoices(companyId, s, documentStatus, accountingStatus, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<SupplierInvoiceList>(nameof(ListSupplierInvoices));
        procurement.MapGet("/supplier-invoices/{supplierInvoiceId:guid}", (HttpContext http, Guid companyId, Guid supplierInvoiceId, GetSupplierInvoiceHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetSupplierInvoice(companyId, s, supplierInvoiceId), handler, ct))
            .Describe<SupplierInvoiceDetail>(nameof(GetSupplierInvoice), notFound: true);

        // USD1-04 (E-USD1-04-1…7): DUAs and import settlements.
        procurement.MapGet("/customs-declarations", (HttpContext http, Guid companyId, string? status, int? limit, int? offset, ListCustomsDeclarationsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListCustomsDeclarations(companyId, s, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<CustomsDeclarationList>(nameof(ListCustomsDeclarations));
        procurement.MapGet("/import-settlements", (HttpContext http, Guid companyId, string? status, int? limit, int? offset, ListImportSettlementsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListImportSettlements(companyId, s, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ImportSettlementList>(nameof(ListImportSettlements));
        procurement.MapGet("/import-settlements/{settlementId:guid}", (HttpContext http, Guid companyId, Guid settlementId, GetImportSettlementHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetImportSettlement(companyId, s, settlementId), handler, ct))
            .Describe<ImportSettlementDetail>(nameof(GetImportSettlement), notFound: true);

        var reconciliation = company.MapGroup("/reconciliation").WithTags("Reconciliation");
        reconciliation.MapGet("/setup-status", (HttpContext http, Guid companyId, GetSetupStatusHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetSetupStatus(companyId, s), handler, ct))
            .Describe<SetupStatus>(nameof(GetSetupStatus));
        reconciliation.MapGet("/periods", (HttpContext http, Guid companyId, int year, ListPeriodsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPeriods(companyId, s, year), handler, ct))
            .Describe<PeriodList>(nameof(ListPeriods));
        reconciliation.MapGet("/runs", (HttpContext http, Guid companyId, string? reconCode, int? limit, int? offset, ListReconciliationRunsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReconciliationRuns(companyId, s, reconCode, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ReconciliationRunList>(nameof(ListReconciliationRuns));
        // E-UX4-2: each reconciliation's latest run.
        reconciliation.MapGet("/runs/latest", (HttpContext http, Guid companyId, ListLatestReconciliationRunsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListLatestReconciliationRuns(companyId, s), handler, ct))
            .Describe<LatestReconciliationRunList>(nameof(ListLatestReconciliationRuns));
        reconciliation.MapGet("/runs/{runId:guid}", (HttpContext http, Guid companyId, Guid runId, GetReconciliationRunHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetReconciliationRun(companyId, s, runId), handler, ct))
            .Describe<ReconciliationRunDetail>(nameof(GetReconciliationRun), notFound: true);
        // E-UX3-1/2: whether each component of a period can close; the reconciliations in words.
        reconciliation.MapGet("/periods/{periodId:guid}/close-readiness", (HttpContext http, Guid companyId, Guid periodId, GetCloseReadinessHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCloseReadiness(companyId, s, periodId), handler, ct))
            .Describe<CloseReadiness>(nameof(GetCloseReadiness), notFound: true);
        reconciliation.MapGet("/definitions", (HttpContext http, Guid companyId, ListReconciliationDefinitionsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReconciliationDefinitions(companyId, s), handler, ct))
            .Describe<ReconciliationDefinitionList>(nameof(ListReconciliationDefinitions));

        // E-MFG1-02-9: production master data (production:read).
        var manufacturing = company.MapGroup("/manufacturing").WithTags("Manufacturing");
        manufacturing.MapGet("/machines", (HttpContext http, Guid companyId, Guid? plantId, string? status, ListMachinesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListMachines(companyId, s, plantId, status), handler, ct))
            .Describe<MachineList>(nameof(ListMachines));
        manufacturing.MapGet("/shifts", (HttpContext http, Guid companyId, Guid? plantId, string? status, ListShiftsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListShifts(companyId, s, plantId, status), handler, ct))
            .Describe<ShiftList>(nameof(ListShifts));
        manufacturing.MapGet("/recipes", (HttpContext http, Guid companyId, Guid? plantId, Guid? itemId, string? status, ListRecipesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListRecipes(companyId, s, plantId, itemId, status), handler, ct))
            .Describe<RecipeList>(nameof(ListRecipes));
        manufacturing.MapGet("/recipes/{recipeVersionId:guid}", (HttpContext http, Guid companyId, Guid recipeVersionId, GetRecipeHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetRecipe(companyId, s, recipeVersionId), handler, ct))
            .Describe<RecipeDetail>(nameof(GetRecipe), notFound: true);
        manufacturing.MapGet("/runs", (HttpContext http, Guid companyId, Guid? plantId, DateOnly? businessDate, string? status, int? limit, int? offset, ListProductionRunsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListProductionRuns(companyId, s, plantId, businessDate, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ProductionRunList>(nameof(ListProductionRuns));
        manufacturing.MapGet("/runs/{runId:guid}", (HttpContext http, Guid companyId, Guid runId, GetProductionRunHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetProductionRun(companyId, s, runId), handler, ct))
            .Describe<ProductionRunDetail>(nameof(GetProductionRun), notFound: true);
        manufacturing.MapGet("/lots", (HttpContext http, Guid companyId, Guid? plantId, string? status, int? limit, int? offset, ListFgLotsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListFgLots(companyId, s, plantId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<FgLotList>(nameof(ListFgLots));
        manufacturing.MapGet("/cost-collectors", (HttpContext http, Guid companyId, Guid? plantId, DateOnly? month, string? status, ListCostCollectorsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListCostCollectors(companyId, s, plantId, month, status), handler, ct))
            .Describe<CostCollectorList>(nameof(ListCostCollectors));
        manufacturing.MapGet("/production-day", (HttpContext http, Guid companyId, Guid plantId, DateOnly businessDate, GetProductionDayHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetProductionDay(companyId, s, plantId, businessDate), handler, ct))
            .Describe<ProductionDay>(nameof(GetProductionDay));

        // E-VS3-02-11: VS#3 master data (sales:read).
        var sales = company.MapGroup("/sales").WithTags("Sales");
        sales.MapGet("/plants", (HttpContext http, Guid companyId, ListSalesPlantsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSalesPlants(companyId, s), handler, ct))
            .Describe<SalesPlantList>(nameof(ListSalesPlants));
        sales.MapGet("/bank-accounts", (HttpContext http, Guid companyId, ListSalesBankAccountsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSalesBankAccounts(companyId, s), handler, ct))
            .Describe<SalesBankAccountList>(nameof(ListSalesBankAccounts));
        sales.MapGet("/customers", (HttpContext http, Guid companyId, string? status, string? search, int? limit, int? offset, ListCustomersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListCustomers(companyId, s, status, search, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<CustomerList>(nameof(ListCustomers));
        sales.MapGet("/customers/{partyId:guid}", (HttpContext http, Guid companyId, Guid partyId, GetCustomerHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCustomer(companyId, s, partyId), handler, ct))
            .Describe<CustomerDetail>(nameof(GetCustomer), notFound: true);
        sales.MapGet("/customer-terms", (HttpContext http, Guid companyId, string? status, ListCustomerTermsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListCustomerTerms(companyId, s, status), handler, ct))
            .Describe<CustomerTermsList>(nameof(ListCustomerTerms));
        sales.MapGet("/standard-costs", (HttpContext http, Guid companyId, string? status, Guid? itemId, ListStandardCostsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListStandardCosts(companyId, s, status, itemId), handler, ct))
            .Describe<StandardCostList>(nameof(ListStandardCosts));
        sales.MapGet("/price-lists", (HttpContext http, Guid companyId, Guid? priceListId, ListPriceListsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPriceLists(companyId, s, priceListId), handler, ct))
            .Describe<PriceListList>(nameof(ListPriceLists));
        // PRS-03 (E-PRS-03-6): delivery zones.
        sales.MapGet("/delivery-zones", (HttpContext http, Guid companyId, string? status, ListDeliveryZonesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDeliveryZones(companyId, s, status), handler, ct))
            .Describe<DeliveryZoneList>(nameof(ListDeliveryZones));
        // PRS-02 (E-PRC1-11): the named lists.
        sales.MapGet("/price-list-headers", (HttpContext http, Guid companyId, ListPriceListHeadersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPriceListHeaders(companyId, s), handler, ct))
            .Describe<PriceListHeaderList>(nameof(ListPriceListHeaders));
        sales.MapGet("/price-lists/{priceListVersionId:guid}", (HttpContext http, Guid companyId, Guid priceListVersionId, GetPriceListHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPriceList(companyId, s, priceListVersionId), handler, ct))
            .Describe<PriceListDetail>(nameof(GetPriceList), notFound: true);
        sales.MapGet("/vehicles", (HttpContext http, Guid companyId, string? status, ListVehiclesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListVehicles(companyId, s, status), handler, ct))
            .Describe<VehicleList>(nameof(ListVehicles));
        sales.MapGet("/drivers", (HttpContext http, Guid companyId, string? status, ListDriversHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDrivers(companyId, s, status), handler, ct))
            .Describe<DriverList>(nameof(ListDrivers));
        sales.MapGet("/opening-batches", (HttpContext http, Guid companyId, string? status, ListOpeningBatchesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListOpeningBatches(companyId, s, status), handler, ct))
            .Describe<OpeningBatchList>(nameof(ListOpeningBatches));
        sales.MapGet("/opening-batches/{batchId:guid}", (HttpContext http, Guid companyId, Guid batchId, GetOpeningBatchHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetOpeningBatch(companyId, s, batchId), handler, ct))
            .Describe<OpeningBatchDetail>(nameof(GetOpeningBatch), notFound: true);
        sales.MapGet("/orders", (HttpContext http, Guid companyId, string? status, Guid? partyId, int? limit, int? offset, DateOnly? from, DateOnly? to, bool? cashSale, ListSalesOrdersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSalesOrders(companyId, s, status, partyId, limit ?? DefaultLimit, offset ?? 0, from, to, cashSale), handler, ct))
            .Describe<SalesOrderList>(nameof(ListSalesOrders));
        sales.MapGet("/orders/{salesOrderId:guid}", (HttpContext http, Guid companyId, Guid salesOrderId, GetSalesOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetSalesOrder(companyId, s, salesOrderId), handler, ct))
            .Describe<SalesOrderDetail>(nameof(GetSalesOrder), notFound: true);
        // E-CF1-05-6: the amount from which a cash sale must identify its buyer.
        sales.MapGet("/cash-sale-setup", (HttpContext http, Guid companyId, GetCashSaleSetupHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCashSaleSetup(companyId, s), handler, ct))
            .Describe<CashSaleSetup>(nameof(GetCashSaleSetup));
        // E-FIS1b-9: the proformas (collection documents of deliveries whose exemption is in process).
        sales.MapGet("/proformas", (HttpContext http, Guid companyId, Guid? partyId, string? status, int? limit, int? offset, ListProformasHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListProformas(companyId, s, partyId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ProformaList>(nameof(ListProformas));
        sales.MapGet("/proformas/{proformaId:guid}", (HttpContext http, Guid companyId, Guid proformaId, GetProformaHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetProforma(companyId, s, proformaId), handler, ct))
            .Describe<ProformaDetail>(nameof(GetProforma), notFound: true);
        // MAIL-02 (E-MAIL-7, E-MAIL-01-6): the sendings of a document and the exact PDF sent.
        sales.MapGet("/mail", (HttpContext http, Guid companyId, string documentType, Guid documentId, ListDocumentMailHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDocumentMail(companyId, s, documentType, documentId), handler, ct))
            .Describe<DocumentMailList>(nameof(ListDocumentMail));
        sales.MapGet("/mail/{mailId:guid}/pdf", (HttpContext http, Guid companyId, Guid mailId, GetDocumentMailPdfHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetDocumentMailPdf(companyId, s, mailId), handler, ct))
            .Describe<DocumentMailPdf>(nameof(GetDocumentMailPdf), notFound: true);
        // E-FIS1b-8: refunds of customers' credit balances.
        sales.MapGet("/customer-refunds", (HttpContext http, Guid companyId, Guid? partyId, string? status, int? limit, int? offset, ListCustomerRefundsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListCustomerRefunds(companyId, s, partyId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<CustomerRefundList>(nameof(ListCustomerRefunds));
        sales.MapGet("/customer-refunds/{refundId:guid}", (HttpContext http, Guid companyId, Guid refundId, GetCustomerRefundHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCustomerRefund(companyId, s, refundId), handler, ct))
            .Describe<CustomerRefundDetail>(nameof(GetCustomerRefund), notFound: true);
        sales.MapGet("/quotes", (HttpContext http, Guid companyId, string? status, Guid? partyId, bool? expiredOnly, int? limit, int? offset, ListQuotesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListQuotes(companyId, s, status, partyId, expiredOnly ?? false, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<QuoteList>(nameof(ListQuotes));
        sales.MapGet("/quotes/{quoteId:guid}", (HttpContext http, Guid companyId, Guid quoteId, GetQuoteHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetQuote(companyId, s, quoteId), handler, ct))
            .Describe<QuoteDetail>(nameof(GetQuote), notFound: true);
        sales.MapGet("/quotes/{quoteId:guid}/print", (HttpContext http, Guid companyId, Guid quoteId, GetQuotePrintHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetQuotePrint(companyId, s, quoteId), handler, ct))
            .Describe<QuotePrint>(nameof(GetQuotePrint), notFound: true);
        // E-UX4-3/4/10: previews of a draft order or quote (POST: the lines travel in the body; read-only), the credit an order would
        // use and how a receipt would be applied.
        sales.MapPost("/orders/preview", (HttpContext http, Guid companyId, PreviewSalesOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<SalesOrderPreviewRequest, PreviewSalesOrder>(http, (s, b) => new PreviewSalesOrder(companyId, s, b.PlantId, b.Lines, b.PartyId, b.DeliveryZoneId, b.ExemptionPending), handler, ct))
            .Describe<SalesPreview>(nameof(PreviewSalesOrder))
            .Accepts<SalesOrderPreviewRequest>("application/json");
        sales.MapPost("/cash-sales/preview", (HttpContext http, Guid companyId, PreviewCashSaleHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<SalesOrderPreviewRequest, PreviewCashSale>(http, (s, b) => new PreviewCashSale(companyId, s, b.PlantId, b.Lines, b.DeliveryZoneId), handler, ct))
            .Describe<SalesPreview>(nameof(PreviewCashSale))
            .Accepts<SalesOrderPreviewRequest>("application/json");
        sales.MapPost("/quotes/preview", (HttpContext http, Guid companyId, PreviewQuoteHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<QuotePreviewRequest, PreviewQuote>(http, (s, b) => new PreviewQuote(companyId, s, b.PlantId, b.Lines, b.PartyId, b.DeliveryZoneId), handler, ct))
            .Describe<SalesPreview>(nameof(PreviewQuote))
            .Accepts<QuotePreviewRequest>("application/json");
        // E-IMP-1: what the customer file would load (read-only).
        sales.MapPost("/customers/import-preview", (HttpContext http, Guid companyId, PreviewCustomerImportHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunBodyAsync<PartyImportRequest, PreviewCustomerImport>(http, (s, b) => new PreviewCustomerImport(companyId, s, b.FileName, b.ContentBase64), handler, ct))
            .Describe<PartyImportPreview>(nameof(PreviewCustomerImport))
            .Accepts<PartyImportRequest>("application/json");
        sales.MapGet("/customers/{partyId:guid}/credit-preview", (HttpContext http, Guid companyId, Guid partyId, string amount, GetCreditPreviewHandler handler, QueryRunner runner, CancellationToken ct)
                => TryAmount(amount, out var value)
                    ? runner.RunAsync(http, s => new GetCreditPreview(companyId, s, partyId, value), handler, ct)
                    : Task.FromResult(InvalidAmount(http)))
            .Describe<CreditPreview>(nameof(GetCreditPreview), notFound: true);
        sales.MapGet("/customers/{partyId:guid}/receipt-application-suggestion", (HttpContext http, Guid companyId, Guid partyId, string amount, SuggestReceiptApplicationHandler handler, QueryRunner runner, CancellationToken ct)
                => TryAmount(amount, out var value)
                    ? runner.RunAsync(http, s => new SuggestReceiptApplication(companyId, s, partyId, value), handler, ct)
                    : Task.FromResult(InvalidAmount(http)))
            .Describe<ReceiptApplicationSuggestion>(nameof(SuggestReceiptApplication));
        sales.MapGet("/customers/{partyId:guid}/exposure", (HttpContext http, Guid companyId, Guid partyId, GetCustomerExposureHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCustomerExposure(companyId, s, partyId), handler, ct))
            .Describe<CustomerExposure>(nameof(GetCustomerExposure), notFound: true);

        // E-VS3-09-1…4: AR aging and the statement of account; ?format=csv as the ledger reports.
        sales.MapGet("/ar-aging", (HttpContext http, Guid companyId, DateOnly? asOf, string? format, GetArAgingHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetArAging(companyId, s, asOf), handler, ArCsv.Aging, asOf is { } day ? $"antiguedad-cxc-{day:yyyyMMdd}.csv" : "antiguedad-cxc.csv", ct))
            .Describe<ArAging>(nameof(GetArAging)).Csv<ArAging>();
        sales.MapGet("/customers/{partyId:guid}/statement", (HttpContext http, Guid companyId, Guid partyId, DateOnly from, DateOnly to, string? format, GetCustomerStatementHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetCustomerStatement(companyId, s, partyId, from, to), handler, ArCsv.Statement, $"estado-de-cuenta-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", ct))
            .Describe<CustomerStatement>(nameof(GetCustomerStatement), notFound: true).Csv<CustomerStatement>();
        sales.MapGet("/deliveries", (HttpContext http, Guid companyId, string? status, Guid? salesOrderId, int? limit, int? offset, Guid? partyId, DateOnly? from, DateOnly? to, Guid? vehicleId, Guid? driverId, ListDeliveriesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDeliveries(companyId, s, status, salesOrderId, limit ?? DefaultLimit, offset ?? 0, partyId, from, to, vehicleId, driverId), handler, ct))
            .Describe<DeliveryList>(nameof(ListDeliveries));
        sales.MapGet("/deliveries/{deliveryId:guid}", (HttpContext http, Guid companyId, Guid deliveryId, GetDeliveryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetDelivery(companyId, s, deliveryId), handler, ct))
            .Describe<DeliveryDetail>(nameof(GetDelivery), notFound: true);
        // E-UX3-7: the printable delivery note.
        sales.MapGet("/deliveries/{deliveryId:guid}/print", (HttpContext http, Guid companyId, Guid deliveryId, GetDeliveryPrintHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetDeliveryPrint(companyId, s, deliveryId), handler, ct))
            .Describe<DeliveryPrint>(nameof(GetDeliveryPrint), notFound: true);
        sales.MapGet("/invoices", (HttpContext http, Guid companyId, string? commercialStatus, string? fiscalStatus, Guid? partyId, int? limit, int? offset, bool? openOnly, ListInvoicesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListInvoices(companyId, s, commercialStatus, fiscalStatus, partyId, limit ?? DefaultLimit, offset ?? 0, openOnly ?? false), handler, ct))
            .Describe<InvoiceList>(nameof(ListInvoices));
        sales.MapGet("/invoices/{invoiceId:guid}", (HttpContext http, Guid companyId, Guid invoiceId, GetInvoiceHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetInvoice(companyId, s, invoiceId), handler, ct))
            .Describe<InvoiceDetail>(nameof(GetInvoice), notFound: true);
        sales.MapGet("/invoices/{invoiceId:guid}/fiscal-package", (HttpContext http, Guid companyId, Guid invoiceId, GetInvoiceFiscalPackageHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetInvoiceFiscalPackage(companyId, s, invoiceId), handler, ct))
            .Describe<InvoiceFiscalPackage>(nameof(GetInvoiceFiscalPackage), notFound: true);
        sales.MapGet("/billable-deliveries", (HttpContext http, Guid companyId, Guid? partyId, ListBillableDeliveriesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListBillableDeliveries(companyId, s, partyId), handler, ct))
            .Describe<BillableDeliveryList>(nameof(ListBillableDeliveries));
        sales.MapGet("/credit-notes", (HttpContext http, Guid companyId, Guid? invoiceId, string? fiscalStatus, Guid? partyId, int? limit, int? offset, ListCreditNotesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListCreditNotes(companyId, s, invoiceId, fiscalStatus, partyId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<CreditNoteList>(nameof(ListCreditNotes));
        sales.MapGet("/credit-notes/{creditNoteId:guid}", (HttpContext http, Guid companyId, Guid creditNoteId, GetCreditNoteHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCreditNote(companyId, s, creditNoteId), handler, ct))
            .Describe<CreditNoteDetail>(nameof(GetCreditNote), notFound: true);
        sales.MapGet("/credit-notes/{creditNoteId:guid}/fiscal-package", (HttpContext http, Guid companyId, Guid creditNoteId, GetCreditNoteFiscalPackageHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCreditNoteFiscalPackage(companyId, s, creditNoteId), handler, ct))
            .Describe<CreditNoteFiscalPackage>(nameof(GetCreditNoteFiscalPackage), notFound: true);
        sales.MapGet("/receipts", (HttpContext http, Guid companyId, Guid? partyId, string? status, string? applicationStatus, string? bankStatus, int? limit, int? offset, ListReceiptsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReceipts(companyId, s, partyId, status, applicationStatus, bankStatus, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ReceiptList>(nameof(ListReceipts));
        sales.MapGet("/receipts/{receiptId:guid}", (HttpContext http, Guid companyId, Guid receiptId, GetReceiptHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetReceipt(companyId, s, receiptId), handler, ct))
            .Describe<ReceiptDetail>(nameof(GetReceipt), notFound: true);
        sales.MapGet("/deposits", (HttpContext http, Guid companyId, Guid? bankAccountId, string? status, int? limit, int? offset, ListDepositsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDeposits(companyId, s, bankAccountId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<DepositList>(nameof(ListDeposits));
        sales.MapGet("/deposits/{depositId:guid}", (HttpContext http, Guid companyId, Guid depositId, GetDepositHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetDeposit(companyId, s, depositId), handler, ct))
            .Describe<DepositDetail>(nameof(GetDeposit), notFound: true);

        // AF1-02 (E-AF1-02-9): fixed-asset classes and cards (ledger:read).
        var fixedAssets = company.MapGroup("/fixed-assets").WithTags("FixedAssets");
        fixedAssets.MapGet("/classes", (HttpContext http, Guid companyId, string? status, ListAssetClassesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAssetClasses(companyId, s, status), handler, ct))
            .Describe<AssetClassList>(nameof(ListAssetClasses));
        fixedAssets.MapGet(
                "/assets",
                (HttpContext http, Guid companyId, string? status, Guid? plantId, Guid? expenseCategoryId, int? limit, int? offset, ListFixedAssetsHandler handler, QueryRunner runner,
                    CancellationToken ct)
                => runner.RunAsync(http, s => new ListFixedAssets(companyId, s, status, plantId, expenseCategoryId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<FixedAssetList>(nameof(ListFixedAssets));
        fixedAssets.MapGet("/assets/{assetId:guid}", (HttpContext http, Guid companyId, Guid assetId, GetFixedAssetHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetFixedAsset(companyId, s, assetId), handler, ct))
            .Describe<FixedAssetDetail>(nameof(GetFixedAsset), notFound: true);
        fixedAssets.MapGet("/depreciation-runs", (HttpContext http, Guid companyId, ListDepreciationRunsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDepreciationRuns(companyId, s), handler, ct))
            .Describe<DepreciationRunList>(nameof(ListDepreciationRuns)); // AF1-03
        fixedAssets.MapGet("/disposals", (HttpContext http, Guid companyId, string? status, ListAssetDisposalsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAssetDisposals(companyId, s, status), handler, ct))
            .Describe<AssetDisposalList>(nameof(ListAssetDisposals));

        var finance = company.MapGroup("/finance").WithTags("Finance");
        finance.MapGet("/events/{sourceEventId:guid}/journals", (HttpContext http, Guid companyId, Guid sourceEventId, ListEventJournalsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListEventJournals(companyId, s, sourceEventId), handler, ct))
            .Describe<EventJournals>(nameof(ListEventJournals), notFound: true);

        // E-B03-15-1: configuration lists for the approval screens (configuration:read).
        // USD1-02 (E-USD1-02-5): exchange rates.
        finance.MapGet("/exchange-rates", (HttpContext http, Guid companyId, DateOnly? from, DateOnly? to, ListExchangeRatesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListExchangeRates(companyId, s, from, to), handler, ct))
            .Describe<ExchangeRateList>(nameof(ListExchangeRates));
        finance.MapGet("/fx-revaluations", (HttpContext http, Guid companyId, ListFxRevaluationsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListFxRevaluations(companyId, s), handler, ct))
            .Describe<FxRevaluationList>(nameof(ListFxRevaluations)); // USD1-06
        finance.MapGet("/exchange-rates/for-date", (HttpContext http, Guid companyId, DateOnly date, GetExchangeRateForDateHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetExchangeRateForDate(companyId, s, date), handler, ct))
            .Describe<ApplicableRate>(nameof(GetExchangeRateForDate));
        finance.MapGet("/accounts", (HttpContext http, Guid companyId, ListAccountsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAccounts(companyId, s), handler, ct))
            .Describe<AccountList>(nameof(ListAccounts));
        finance.MapGet("/manual-journals", (HttpContext http, Guid companyId, string? status, int? limit, int? offset, ListManualJournalsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListManualJournals(companyId, s, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<ManualJournalList>(nameof(ListManualJournals));
        finance.MapGet("/manual-journals/{manualJournalId:guid}", (HttpContext http, Guid companyId, Guid manualJournalId, GetManualJournalHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetManualJournal(companyId, s, manualJournalId), handler, ct))
            .Describe<ManualJournalDetail>(nameof(GetManualJournal));

        // FIN1-03 (E-FIN1-03-4…11): ?format=csv returns the same report as a CSV file.
        finance.MapGet("/trial-balance", (HttpContext http, Guid companyId, DateOnly from, DateOnly to, Guid? plantId, Guid? partyId, Guid? bankAccountId, string? format, GetTrialBalanceHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetTrialBalance(companyId, s, from, to, plantId, partyId, bankAccountId), handler, LedgerCsv.TrialBalance, $"balanza-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", ct))
            .Describe<TrialBalance>(nameof(GetTrialBalance)).Csv<TrialBalance>();
        finance.MapGet("/accounts/{accountId:guid}/ledger", (HttpContext http, Guid companyId, Guid accountId, DateOnly from, DateOnly to, int? limit, int? offset, string? format, GetAccountLedgerHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetAccountLedger(companyId, s, accountId, from, to, limit ?? DefaultLimit, offset ?? 0, All: format == "csv"), handler, LedgerCsv.AccountLedger, $"mayor-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", ct))
            .Describe<AccountLedger>(nameof(GetAccountLedger), notFound: true).Csv<AccountLedger>();
        finance.MapGet("/balance-sheet", (HttpContext http, Guid companyId, DateOnly asOf, string? format, GetBalanceSheetHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetBalanceSheet(companyId, s, asOf), handler, LedgerCsv.BalanceSheet, $"balance-general-{asOf:yyyyMMdd}.csv", ct))
            .Describe<BalanceSheet>(nameof(GetBalanceSheet)).Csv<BalanceSheet>();
        finance.MapGet("/income-statement", (HttpContext http, Guid companyId, DateOnly from, DateOnly to, string? format, GetIncomeStatementHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetIncomeStatement(companyId, s, from, to), handler, LedgerCsv.IncomeStatement, $"estado-de-resultados-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", ct))
            .Describe<IncomeStatement>(nameof(GetIncomeStatement)).Csv<IncomeStatement>();
        finance.MapGet("/report-structures", (HttpContext http, Guid companyId, string? report, ListReportStructuresHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReportStructures(companyId, s, report), handler, ct))
            .Describe<ReportStructureList>(nameof(ListReportStructures));
        finance.MapGet("/report-structures/{structureVersionId:guid}", (HttpContext http, Guid companyId, Guid structureVersionId, GetReportStructureHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetReportStructure(companyId, s, structureVersionId), handler, ct))
            .Describe<ReportStructureDetail>(nameof(GetReportStructure), notFound: true);
        finance.MapGet("/account-role-maps", (HttpContext http, Guid companyId, string? status, ListAccountRoleMapsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAccountRoleMaps(companyId, s, status), handler, ct))
            .Describe<AccountRoleMapList>(nameof(ListAccountRoleMaps));
        finance.MapGet("/account-roles", (HttpContext http, Guid companyId, ListAccountRolesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListAccountRoles(companyId, s), handler, ct))
            .Describe<AccountRoleList>(nameof(ListAccountRoles));
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
        // E-GAS-02-7: the tax types in force on a date, for the expense lines of purchase orders and supplier invoices.
        tax.MapGet("/purchase-tax-types", (HttpContext http, Guid companyId, DateOnly? date, ListPurchaseTaxTypesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPurchaseTaxTypes(companyId, s, date), handler, ct))
            .Describe<PurchaseTaxTypeList>(nameof(ListPurchaseTaxTypes));
        // E-FIS2-02-1…7: the 606 (?format=csv: the DGII tool's columns, no BOM) and the IT-1 / IR-17 summaries (fiscal_report:read).
        tax.MapGet("/reports/606", (HttpContext http, Guid companyId, string period, string? format, GetReport606Handler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunReportAsync(http, format, s => new GetReport606(companyId, s, period), handler, Report606Csv.Build, $"606-{period}.csv", ct, byteOrderMark: false))
            .Describe<Report606>(nameof(GetReport606));
        tax.MapGet("/reports/it1-summary", (HttpContext http, Guid companyId, string period, GetIt1SummaryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetIt1Summary(companyId, s, period), handler, ct))
            .Describe<It1Summary>(nameof(GetIt1Summary));
        tax.MapGet("/reports/ir17-summary", (HttpContext http, Guid companyId, string period, GetIr17SummaryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetIr17Summary(companyId, s, period), handler, ct))
            .Describe<Ir17Summary>(nameof(GetIr17Summary));
        // E-FIS1-02-7/8: fiscal authorizations and the order's proforma (sales:read).
        tax.MapGet("/fiscal-authorizations", (HttpContext http, Guid companyId, Guid? partyId, string? status, ListFiscalAuthorizationsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListFiscalAuthorizations(companyId, s, partyId, status), handler, ct))
            .Describe<FiscalAuthorizationList>(nameof(ListFiscalAuthorizations));
        tax.MapGet("/fiscal-authorizations/{authorizationId:guid}", (HttpContext http, Guid companyId, Guid authorizationId, GetFiscalAuthorizationHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetFiscalAuthorization(companyId, s, authorizationId), handler, ct))
            .Describe<FiscalAuthorizationDetail>(nameof(GetFiscalAuthorization), notFound: true);
        tax.MapGet("/proformas/{salesOrderId:guid}", (HttpContext http, Guid companyId, Guid salesOrderId, GetSalesOrderProformaHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetSalesOrderProforma(companyId, s, salesOrderId), handler, ct))
            .Describe<SalesOrderProforma>(nameof(GetSalesOrderProforma), notFound: true);

        // E-VS2-05-6 / E-VS2-07-1: treasury read side.
        var treasury = company.MapGroup("/treasury").WithTags("Treasury");
        treasury.MapGet("/bank-statements/{statementId:guid}/match-suggestions", (HttpContext http, Guid companyId, Guid statementId, SuggestBankMatchesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new SuggestBankMatches(companyId, s, statementId), handler, ct))
            .Describe<MatchSuggestions>(nameof(SuggestBankMatches), notFound: true);
        treasury.MapGet("/bank-statement-lines/{lineId:guid}/receipt-candidates", (HttpContext http, Guid companyId, Guid lineId, ListReceiptCandidatesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListReceiptCandidates(companyId, s, lineId), handler, ct))
            .Describe<ReceiptCandidates>(nameof(ListReceiptCandidates), notFound: true);
        treasury.MapGet("/ap-aging", (HttpContext http, Guid companyId, DateOnly? asOf, GetApAgingHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetApAging(companyId, s, asOf), handler, ct))
            .Describe<ApAging>(nameof(GetApAging));
        treasury.MapGet("/payment-proposal", (HttpContext http, Guid companyId, DateOnly dueUntil, Guid? supplierId, GetPaymentProposalHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPaymentProposal(companyId, s, dueUntil, supplierId), handler, ct))
            .Describe<PaymentProposal>(nameof(GetPaymentProposal));
        treasury.MapGet("/bank-transfers", (HttpContext http, Guid companyId, string? status, int? limit, int? offset, ListBankTransfersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListBankTransfers(companyId, s, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<BankTransferList>(nameof(ListBankTransfers)); // USD1-05b (E-USD1-05b-1)
        treasury.MapGet("/payments", (HttpContext http, Guid companyId, string? status, Guid? supplierId, int? limit, int? offset, ListPaymentsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPayments(companyId, s, status, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<PaymentList>(nameof(ListPayments));
        treasury.MapGet("/payments/{paymentId:guid}", (HttpContext http, Guid companyId, Guid paymentId, GetPaymentHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPayment(companyId, s, paymentId), handler, ct))
            .Describe<PaymentDetail>(nameof(GetPayment), notFound: true);
        // E-FIS1b-01-9: released customer refunds waiting for their statement line.
        treasury.MapGet("/refunds-to-match", (HttpContext http, Guid companyId, Guid? bankAccountId, ListRefundsToMatchHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListRefundsToMatch(companyId, s, bankAccountId), handler, ct))
            .Describe<RefundToMatchList>(nameof(ListRefundsToMatch));
        treasury.MapGet("/bank-accounts", (HttpContext http, Guid companyId, ListBankAccountsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListBankAccounts(companyId, s), handler, ct))
            .Describe<BankAccountList>(nameof(ListBankAccounts));
        treasury.MapGet("/bank-accounts/{bankAccountId:guid}/reconciliation", (HttpContext http, Guid companyId, Guid bankAccountId, DateOnly? asOf, GetBankReconciliationHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetBankReconciliation(companyId, s, bankAccountId, asOf), handler, ct))
            .Describe<BankReconciliationView>(nameof(GetBankReconciliation), notFound: true);
        treasury.MapGet("/suppliers/{partyId:guid}/bank-accounts", (HttpContext http, Guid companyId, Guid partyId, ListPartyBankAccountsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPartyBankAccounts(companyId, s, partyId), handler, ct))
            .Describe<PartyBankAccountList>(nameof(ListPartyBankAccounts));
        treasury.MapGet("/bank-statements", (HttpContext http, Guid companyId, Guid? bankAccountId, int? limit, int? offset, ListBankStatementsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListBankStatements(companyId, s, bankAccountId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<BankStatementList>(nameof(ListBankStatements));
        treasury.MapGet("/bank-statement-lines", (HttpContext http, Guid companyId, Guid? statementId, Guid? bankAccountId, string? status, int? limit, int? offset, ListBankStatementLinesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListBankStatementLines(companyId, s, statementId, bankAccountId, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<BankStatementLineList>(nameof(ListBankStatementLines));

        // E-UI01-3/4: users, role change requests and the digests written to WORM.
        var identity = company.MapGroup("/identity").WithTags("Identity");
        identity.MapGet("/roles", (HttpContext http, Guid companyId, ListRolesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListRoles(companyId, s), handler, ct))
            .Describe<RoleList>(nameof(ListRoles));
        identity.MapGet("/users", (HttpContext http, Guid companyId, ListUsersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListUsers(companyId, s), handler, ct))
            .Describe<UserList>(nameof(ListUsers));
        identity.MapGet("/role-requests", (HttpContext http, Guid companyId, string? status, int? limit, int? offset, ListRoleRequestsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListRoleRequests(companyId, s, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<RoleRequestList>(nameof(ListRoleRequests));
        var audit = company.MapGroup("/audit").WithTags("Audit");
        audit.MapGet("/digests", (HttpContext http, Guid companyId, int? limit, int? offset, ListLedgerDigestsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListLedgerDigests(companyId, s, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<LedgerDigestList>(nameof(ListLedgerDigests));

        // E-UX4-15: journals by document number or id, and the last verification of the hash chains.
        audit.MapGet("/journals", (HttpContext http, Guid companyId, string text, int? limit, int? offset, SearchJournalsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new SearchJournals(companyId, s, text, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<JournalSearchResult>(nameof(SearchJournals));
        audit.MapGet("/integrity-status", (HttpContext http, Guid companyId, GetIntegrityStatusHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetIntegrityStatus(companyId, s), handler, ct))
            .Describe<IntegrityStatus>(nameof(GetIntegrityStatus));

        // E-PR17-6: the HTTP endpoint of "Explain this entry". Its result is the PR-17 document, returned as is.
        finance.MapGet("/entries/{glEntryId:guid}/explanation", (HttpContext http, Guid companyId, Guid glEntryId, ExplainEntryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ExplainEntry(companyId, s, glEntryId), handler, ct))
            .Describe<JsonElement>(nameof(ExplainEntry), notFound: true);
    }

    /// <summary>A decimal amount in the query string, as the API writes decimals: digits with an optional point (E-UX4-4/10).</summary>
    private static bool TryAmount(string? value, out decimal amount)
        => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);

    private static IResult InvalidAmount(HttpContext http)
        => ApiProblems.Problem(http, Platform.Queries.QueryErrors.InvalidParameter, "amount must be a decimal number such as 1500.00.", isQuery: true);

    /// <summary>The report is also served as text/csv with <c>?format=csv</c> (the same report, see LedgerCsv).</summary>
    private static RouteHandlerBuilder Csv<TResult>(this RouteHandlerBuilder endpoint) => endpoint.Produces<TResult>(StatusCodes.Status200OK, "application/json", "text/csv");

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
