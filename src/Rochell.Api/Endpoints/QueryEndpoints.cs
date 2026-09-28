using System.Text.Json;
using Rochell.Api.Http;
using Rochell.Audit;
using Rochell.Finance.Configuration;
using Rochell.Finance.Explain;
using Rochell.Finance.Ledger;
using Rochell.Finance.Policies;
using Rochell.Identity.Queries;
using Rochell.MasterData.Queries;
using Rochell.Platform.Queries;
using Rochell.Procurement.Queries;
using Rochell.Reconciliation.Queries;
using Rochell.Sales.Queries;
using Rochell.Tax;
using Rochell.Treasury.Queries;
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
        typeof(ListManualJournalsHandler), typeof(GetManualJournalHandler), typeof(GetTrialBalanceHandler), typeof(GetAccountLedgerHandler),
        typeof(GetBalanceSheetHandler), typeof(GetIncomeStatementHandler), typeof(ListReportStructuresHandler), typeof(GetReportStructureHandler),
        typeof(ListFiscalSourcesHandler), typeof(ListFiscalRulesHandler), typeof(SuggestBankMatchesHandler),
        typeof(GetApAgingHandler), typeof(GetPaymentProposalHandler), typeof(ListPaymentsHandler), typeof(GetPaymentHandler), typeof(ListBankAccountsHandler),
        typeof(ListPartyBankAccountsHandler), typeof(ListBankStatementsHandler), typeof(ListBankStatementLinesHandler), typeof(GetBankReconciliationHandler),
        typeof(ListCustomersHandler), typeof(GetCustomerHandler), typeof(ListCustomerTermsHandler), typeof(ListStandardCostsHandler), typeof(ListPriceListsHandler),
        typeof(GetPriceListHandler), typeof(ListVehiclesHandler), typeof(ListDriversHandler), typeof(ListOpeningBatchesHandler), typeof(GetOpeningBatchHandler),
        typeof(ListSalesOrdersHandler), typeof(GetSalesOrderHandler), typeof(GetCustomerExposureHandler), typeof(ListDeliveriesHandler), typeof(GetDeliveryHandler),
        typeof(ListInvoicesHandler), typeof(GetInvoiceHandler), typeof(GetInvoiceFiscalPackageHandler), typeof(ListBillableDeliveriesHandler),
        typeof(ListCreditNotesHandler), typeof(GetCreditNoteHandler), typeof(GetCreditNoteFiscalPackageHandler),
        typeof(ListReceiptsHandler), typeof(GetReceiptHandler), typeof(ListDepositsHandler), typeof(GetDepositHandler),
        typeof(ListUsersHandler), typeof(ListRoleRequestsHandler), typeof(ListLedgerDigestsHandler),
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

        // E-VS3-02-11: VS#3 master data (sales:read).
        var sales = company.MapGroup("/sales").WithTags("Sales");
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
        sales.MapGet("/price-lists", (HttpContext http, Guid companyId, ListPriceListsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPriceLists(companyId, s), handler, ct))
            .Describe<PriceListList>(nameof(ListPriceLists));
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
        sales.MapGet("/orders", (HttpContext http, Guid companyId, string? status, Guid? partyId, int? limit, int? offset, ListSalesOrdersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListSalesOrders(companyId, s, status, partyId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<SalesOrderList>(nameof(ListSalesOrders));
        sales.MapGet("/orders/{salesOrderId:guid}", (HttpContext http, Guid companyId, Guid salesOrderId, GetSalesOrderHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetSalesOrder(companyId, s, salesOrderId), handler, ct))
            .Describe<SalesOrderDetail>(nameof(GetSalesOrder), notFound: true);
        sales.MapGet("/customers/{partyId:guid}/exposure", (HttpContext http, Guid companyId, Guid partyId, GetCustomerExposureHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetCustomerExposure(companyId, s, partyId), handler, ct))
            .Describe<CustomerExposure>(nameof(GetCustomerExposure), notFound: true);
        sales.MapGet("/deliveries", (HttpContext http, Guid companyId, string? status, Guid? salesOrderId, int? limit, int? offset, ListDeliveriesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListDeliveries(companyId, s, status, salesOrderId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<DeliveryList>(nameof(ListDeliveries));
        sales.MapGet("/deliveries/{deliveryId:guid}", (HttpContext http, Guid companyId, Guid deliveryId, GetDeliveryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetDelivery(companyId, s, deliveryId), handler, ct))
            .Describe<DeliveryDetail>(nameof(GetDelivery), notFound: true);
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

        var finance = company.MapGroup("/finance").WithTags("Finance");
        finance.MapGet("/events/{sourceEventId:guid}/journals", (HttpContext http, Guid companyId, Guid sourceEventId, ListEventJournalsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListEventJournals(companyId, s, sourceEventId), handler, ct))
            .Describe<EventJournals>(nameof(ListEventJournals), notFound: true);

        // E-B03-15-1: configuration lists for the approval screens (configuration:read).
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

        // E-VS2-05-6 / E-VS2-07-1: treasury read side.
        var treasury = company.MapGroup("/treasury").WithTags("Treasury");
        treasury.MapGet("/bank-statements/{statementId:guid}/match-suggestions", (HttpContext http, Guid companyId, Guid statementId, SuggestBankMatchesHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new SuggestBankMatches(companyId, s, statementId), handler, ct))
            .Describe<MatchSuggestions>(nameof(SuggestBankMatches), notFound: true);
        treasury.MapGet("/ap-aging", (HttpContext http, Guid companyId, DateOnly? asOf, GetApAgingHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetApAging(companyId, s, asOf), handler, ct))
            .Describe<ApAging>(nameof(GetApAging));
        treasury.MapGet("/payment-proposal", (HttpContext http, Guid companyId, DateOnly dueUntil, Guid? supplierId, GetPaymentProposalHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPaymentProposal(companyId, s, dueUntil, supplierId), handler, ct))
            .Describe<PaymentProposal>(nameof(GetPaymentProposal));
        treasury.MapGet("/payments", (HttpContext http, Guid companyId, string? status, Guid? supplierId, int? limit, int? offset, ListPaymentsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListPayments(companyId, s, status, supplierId, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<PaymentList>(nameof(ListPayments));
        treasury.MapGet("/payments/{paymentId:guid}", (HttpContext http, Guid companyId, Guid paymentId, GetPaymentHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new GetPayment(companyId, s, paymentId), handler, ct))
            .Describe<PaymentDetail>(nameof(GetPayment), notFound: true);
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
        identity.MapGet("/users", (HttpContext http, Guid companyId, ListUsersHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListUsers(companyId, s), handler, ct))
            .Describe<UserList>(nameof(ListUsers));
        identity.MapGet("/role-requests", (HttpContext http, Guid companyId, string? status, int? limit, int? offset, ListRoleRequestsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListRoleRequests(companyId, s, status, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<RoleRequestList>(nameof(ListRoleRequests));
        company.MapGroup("/audit").WithTags("Audit")
            .MapGet("/digests", (HttpContext http, Guid companyId, int? limit, int? offset, ListLedgerDigestsHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ListLedgerDigests(companyId, s, limit ?? DefaultLimit, offset ?? 0), handler, ct))
            .Describe<LedgerDigestList>(nameof(ListLedgerDigests));

        // E-PR17-6: the HTTP endpoint of "Explain this entry". Its result is the PR-17 document, returned as is.
        finance.MapGet("/entries/{glEntryId:guid}/explanation", (HttpContext http, Guid companyId, Guid glEntryId, ExplainEntryHandler handler, QueryRunner runner, CancellationToken ct)
                => runner.RunAsync(http, s => new ExplainEntry(companyId, s, glEntryId), handler, ct))
            .Describe<JsonElement>(nameof(ExplainEntry), notFound: true);
    }

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
