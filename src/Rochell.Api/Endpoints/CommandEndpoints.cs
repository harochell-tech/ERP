using System.Text;
using Rochell.Api.Hosting;
using Rochell.Api.Http;
using Rochell.Audit;
using Rochell.Finance.Configuration;
using Rochell.Finance.Ledger;
using Rochell.Finance.Policies;
using Rochell.Identity.RoleChanges;
using Rochell.MasterData.BankAccounts;
using Rochell.MasterData.Company;
using Rochell.MasterData.Items;
using Rochell.Manufacturing.Costing;
using Rochell.Manufacturing.Lots;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shifts;
using Rochell.MasterData.Suppliers;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.Ledger;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.CashSales;
using Rochell.Sales.Customers;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Fleet;
using Rochell.Sales.Invoices;
using Rochell.Sales.Mail;
using Rochell.Sales.Opening;
using Rochell.Sales.Orders;
using Rochell.Sales.Quotes;
using Rochell.Sales.Zones;
using Rochell.Sales.Pricing;
using Rochell.Sales.Proformas;
using Rochell.Sales.Receipts;
using Rochell.Sales.Refunds;
using Rochell.Tax;
using Rochell.Tax.Authorizations;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Statements;

namespace Rochell.Api.Endpoints;

/// <summary>
/// E-PR18-3: <c>POST /api/v1/companies/{companyId}/{module}/{command}</c> for every production command. The module is the
/// assembly that owns the handler; the command segment is the command type in kebab-case.
/// </summary>
public static class CommandEndpoints
{
    public static void AddCommandHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IRncRegistry>(FormatOnlyRncRegistry.Instance);
        foreach (var handler in Handlers)
        {
            services.AddTransient(handler);
        }
    }

    public static void MapCommandEndpoints(this RouteGroupBuilder company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var masterData = company.MapGroup("/master-data").WithTags("MasterData");
        masterData.MapCommand<UpdateCompanyLegalName, UpdateCompanyLegalNameHandler>();
        masterData.MapCommand<UpdatePlantName, UpdatePlantNameHandler>();
        masterData.MapCommand<CreateSupplier, CreateSupplierHandler>();
        masterData.MapCommand<UpdateSupplier, UpdateSupplierHandler>();
        masterData.MapCommand<ActivateSupplier, ActivateSupplierHandler>();
        masterData.MapCommand<SetSupplierPaymentTerms, SetSupplierPaymentTermsHandler>();
        masterData.MapCommand<SetSupplierContact, SetSupplierContactHandler>();
        masterData.MapCommand<ImportSuppliers, ImportSuppliersHandler>();
        masterData.MapCommand<ActivateSuppliers, ActivateSuppliersHandler>();
        masterData.MapCommand<CreateRawMaterial, CreateRawMaterialHandler>();
        masterData.MapCommand<CreateFinishedGood, CreateFinishedGoodHandler>();
        masterData.MapCommand<CreateFreightItem, CreateFreightItemHandler>(); // PRS-03 (E-PRS-03-1)
        masterData.MapCommand<DefineUomConversion, DefineUomConversionHandler>();
        masterData.MapCommand<ActivateItem, ActivateItemHandler>();
        masterData.MapCommand<RequestPartyBankAccount, RequestPartyBankAccountHandler>();
        masterData.MapCommand<VerifyPartyBankAccount, VerifyPartyBankAccountHandler>();
        masterData.MapCommand<RejectPartyBankAccount, RejectPartyBankAccountHandler>();

        // E-VS2-02-1 / E-VS2-02-8: treasury commands (queries and screens come with VS2-07 / VS2-08).
        var treasury = company.MapGroup("/treasury").WithTags("Treasury");
        treasury.MapCommand<RegisterBankAccount, RegisterBankAccountHandler>();
        treasury.MapCommand<CloseBankAccount, CloseBankAccountHandler>();
        treasury.MapCommand<SetBankAccountAlias, SetBankAccountAliasHandler>(); // E-UX4-6
        treasury.MapCommand<PrepareSupplierPayment, PrepareSupplierPaymentHandler>();
        treasury.MapCommand<UpdatePreparedPayment, UpdatePreparedPaymentHandler>();
        treasury.MapCommand<VoidPayment, VoidPaymentHandler>();
        treasury.MapCommand<ReleaseSupplierPayment, ReleaseSupplierPaymentHandler>();
        treasury.MapCommand<ReversePayment, ReversePaymentHandler>();
        treasury.MapCommand<ImportBankStatement, ImportBankStatementHandler>();
        treasury.MapCommand<MatchBankLine, MatchBankLineHandler>();
        treasury.MapCommand<MatchBankLineToReceipt, MatchBankLineToReceiptHandler>();
        treasury.MapCommand<MatchBankLineToRefund, MatchBankLineToRefundHandler>();
        treasury.MapCommand<UnmatchBankLine, UnmatchBankLineHandler>();
        treasury.MapCommand<RecognizeBankCharge, RecognizeBankChargeHandler>();

        var procurement = company.MapGroup("/procurement").WithTags("Procurement");
        procurement.MapCommand<CreatePurchaseOrder, CreatePurchaseOrderHandler>();
        procurement.MapCommand<UpdatePurchaseOrderDraft, UpdatePurchaseOrderDraftHandler>();
        procurement.MapCommand<SubmitPurchaseOrder, SubmitPurchaseOrderHandler>();
        procurement.MapCommand<ApprovePurchaseOrder, ApprovePurchaseOrderHandler>();
        procurement.MapCommand<RejectPurchaseOrder, RejectPurchaseOrderHandler>();
        procurement.MapCommand<CancelPurchaseOrder, CancelPurchaseOrderHandler>();
        procurement.MapCommand<ApproveOverReceipt, ApproveOverReceiptHandler>();
        procurement.MapCommand<PostGoodsReceipt, PostGoodsReceiptHandler>();
        procurement.MapCommand<ReverseGoodsReceipt, ReverseGoodsReceiptHandler>();
        procurement.MapCommand<CreateReceiptCorrection, CreateReceiptCorrectionHandler>();
        procurement.MapCommand<ApproveReceiptCorrection, ApproveReceiptCorrectionHandler>();
        procurement.MapCommand<RejectReceiptCorrection, RejectReceiptCorrectionHandler>();
        procurement.MapCommand<RegisterSupplierInvoice, RegisterSupplierInvoiceHandler>();
        procurement.MapCommand<RegisterExpenseInvoice, RegisterExpenseInvoiceHandler>(); // GAS1-04 (E-GAS-04-1)
        procurement.MapCommand<CreateExpensePurchaseOrder, CreateExpensePurchaseOrderHandler>(); // GAS1-05 (E-GAS-05-1)
        procurement.MapCommand<UpdateExpensePurchaseOrderDraft, UpdateExpensePurchaseOrderDraftHandler>();
        procurement.MapCommand<CloseExpensePurchaseOrder, CloseExpensePurchaseOrderHandler>();
        // GAS1-03 (E-GAS-03-1…3): expense categories.
        procurement.MapCommand<PrepareExpenseCategory, PrepareExpenseCategoryHandler>();
        procurement.MapCommand<UpdateExpenseCategoryDraft, UpdateExpenseCategoryDraftHandler>();
        procurement.MapCommand<ApproveExpenseCategories, ApproveExpenseCategoriesHandler>();
        procurement.MapCommand<DeactivateExpenseCategory, DeactivateExpenseCategoryHandler>();
        procurement.MapCommand<ReactivateExpenseCategory, ReactivateExpenseCategoryHandler>();
        procurement.MapCommand<MatchSupplierInvoice, MatchSupplierInvoiceHandler>();
        procurement.MapCommand<ApproveMatchException, ApproveMatchExceptionHandler>();
        procurement.MapCommand<VoidSupplierInvoice, VoidSupplierInvoiceHandler>();
        procurement.MapCommand<PostSupplierInvoice, PostSupplierInvoiceHandler>();
        procurement.MapCommand<ReverseSupplierInvoice, ReverseSupplierInvoiceHandler>();
        procurement.MapCommand<RepostEvent, RepostEventHandler>();
        procurement.MapCommand<ApproveValuationResidualAdjustment, ApproveValuationResidualAdjustmentHandler>();

        var finance = company.MapGroup("/finance").WithTags("Finance");
        finance.MapCommand<PrepareAccountRoleMap, PrepareAccountRoleMapHandler>();
        finance.MapCommand<ApproveAccountRoleMap, ApproveAccountRoleMapHandler>();
        finance.MapCommand<ApprovePostingRuleVersion, ApprovePostingRuleVersionHandler>();
        finance.MapCommand<PrepareAccountingPolicyVersion, PrepareAccountingPolicyVersionHandler>();
        finance.MapCommand<ApproveAccountingPolicyVersion, ApproveAccountingPolicyVersionHandler>();
        finance.MapCommand<CreateAccount, CreateAccountHandler>();
        finance.MapCommand<UpdateAccount, UpdateAccountHandler>();
        finance.MapCommand<DeactivateAccount, DeactivateAccountHandler>();
        finance.MapCommand<ActivateAccount, ActivateAccountHandler>();
        finance.MapCommand<PrepareManualJournal, PrepareManualJournalHandler>();
        finance.MapCommand<UpdateManualJournal, UpdateManualJournalHandler>();
        finance.MapCommand<SubmitManualJournal, SubmitManualJournalHandler>();
        finance.MapCommand<WithdrawManualJournal, WithdrawManualJournalHandler>();
        finance.MapCommand<ApproveManualJournal, ApproveManualJournalHandler>();
        finance.MapCommand<RejectManualJournal, RejectManualJournalHandler>();
        finance.MapCommand<ReverseManualJournal, ReverseManualJournalHandler>();
        finance.MapCommand<PrepareReportStructure, PrepareReportStructureHandler>();
        finance.MapCommand<ApproveReportStructure, ApproveReportStructureHandler>();

        var tax = company.MapGroup("/tax").WithTags("Tax");
        tax.MapCommand<RegisterFiscalSource, RegisterFiscalSourceHandler>();
        tax.MapCommand<ConfigureFiscalRuleVersion, ConfigureFiscalRuleVersionHandler>();
        tax.MapCommand<LinkFiscalSource, LinkFiscalSourceHandler>();
        tax.MapCommand<RunFiscalRuleTests, RunFiscalRuleTestsHandler>();
        tax.MapCommand<ActivateFiscalRuleVersion, ActivateFiscalRuleVersionHandler>();
        tax.MapCommand<RegisterFiscalAuthorization, RegisterFiscalAuthorizationHandler>();
        tax.MapCommand<UpdateDraftAuthorization, UpdateDraftAuthorizationHandler>();
        tax.MapCommand<AttachAuthorizationDocument, AttachAuthorizationDocumentHandler>();
        tax.MapCommand<SubmitForVerification, SubmitForVerificationHandler>();
        tax.MapCommand<VerifyAuthorization, VerifyAuthorizationHandler>();
        tax.MapCommand<ReturnAuthorizationToDraft, ReturnAuthorizationToDraftHandler>();
        tax.MapCommand<RejectAuthorization, RejectAuthorizationHandler>();
        tax.MapCommand<SuspendAuthorization, SuspendAuthorizationHandler>();
        tax.MapCommand<ExpireFiscalAuthorizations, ExpireFiscalAuthorizationsHandler>();
        tax.MapCommand<ReactivateAuthorization, ReactivateAuthorizationHandler>();

        var reconciliation = company.MapGroup("/reconciliation").WithTags("Reconciliation");
        reconciliation.MapCommand<RunReconciliation, RunReconciliationHandler>();
        reconciliation.MapCommand<CloseComponent, CloseComponentHandler>();
        reconciliation.MapCommand<RequestReopen, RequestReopenHandler>();
        reconciliation.MapCommand<ApproveReopen, ApproveReopenHandler>();
        reconciliation.MapCommand<RejectReopen, RejectReopenHandler>();

        // The verifier reads the digests from WORM storage: without it (B-03) the endpoint answers 503.
        company.MapGroup("/audit").WithTags("Audit")
            .MapPost("/" + Kebab(nameof(VerifyHashChain)), async (HttpContext http, Guid companyId, HashVerification verification, CommandRunner runner, CancellationToken cancellationToken)
                => await verification.CreateHandlerAsync(cancellationToken).ConfigureAwait(false) is { } handler
                    ? await runner.RunAsync<VerifyHashChain>(http, companyId, handler, cancellationToken).ConfigureAwait(false)
                    : ApiProblems.Problem(http, ApiErrors.ServiceUnavailable, "Hash verification needs WORM storage and the digest public key (B-03)."))
            .Describe<VerifyHashChain>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        var sales = company.MapGroup("/sales").WithTags("Sales");
        sales.MapCommand<CreateCustomer, CreateCustomerHandler>();
        sales.MapCommand<UpdateCustomer, UpdateCustomerHandler>();
        sales.MapCommand<ActivateCustomer, ActivateCustomerHandler>();
        sales.MapCommand<PrepareCustomerTerms, PrepareCustomerTermsHandler>();
        sales.MapCommand<ApproveCustomerTerms, ApproveCustomerTermsHandler>();
        sales.MapCommand<ImportCustomers, ImportCustomersHandler>();
        sales.MapCommand<ApproveCustomerTermsBatch, ApproveCustomerTermsBatchHandler>();
        sales.MapCommand<ActivateCustomers, ActivateCustomersHandler>();
        sales.MapCommand<PrepareStandardCost, PrepareStandardCostHandler>();
        sales.MapCommand<PrepareStandardCostFromRecipe, PrepareStandardCostFromRecipeHandler>();
        sales.MapCommand<ApproveStandardCost, ApproveStandardCostHandler>();
        sales.MapCommand<PreparePriceList, PreparePriceListHandler>();
        sales.MapCommand<ApprovePriceList, ApprovePriceListHandler>();
        sales.MapCommand<CreatePriceList, CreatePriceListHandler>(); // PRS-02 (E-PRC1-5, E-PRS-02-1)
        sales.MapCommand<DeactivatePriceList, DeactivatePriceListHandler>();
        sales.MapCommand<ReactivatePriceList, ReactivatePriceListHandler>();
        sales.MapCommand<CreateDeliveryZone, CreateDeliveryZoneHandler>(); // PRS-03 (E-PRS-03-2)
        sales.MapCommand<RenameDeliveryZone, RenameDeliveryZoneHandler>();
        sales.MapCommand<DeactivateDeliveryZone, DeactivateDeliveryZoneHandler>();
        sales.MapCommand<ReactivateDeliveryZone, ReactivateDeliveryZoneHandler>();
        sales.MapCommand<RegisterVehicle, RegisterVehicleHandler>();
        sales.MapCommand<UpdateVehicle, UpdateVehicleHandler>();
        sales.MapCommand<DeactivateVehicle, DeactivateVehicleHandler>();
        sales.MapCommand<ActivateVehicle, ActivateVehicleHandler>();
        sales.MapCommand<RegisterDriver, RegisterDriverHandler>();
        sales.MapCommand<UpdateDriver, UpdateDriverHandler>();
        sales.MapCommand<DeactivateDriver, DeactivateDriverHandler>();
        sales.MapCommand<ActivateDriver, ActivateDriverHandler>();
        sales.MapCommand<PrepareOpeningInventory, PrepareOpeningInventoryHandler>();
        sales.MapCommand<PostOpeningInventory, PostOpeningInventoryHandler>();
        sales.MapCommand<ReverseOpeningInventory, ReverseOpeningInventoryHandler>();
        sales.MapCommand<CreateSalesOrder, CreateSalesOrderHandler>();
        sales.MapCommand<UpdateSalesOrderDraft, UpdateSalesOrderDraftHandler>();
        sales.MapCommand<SubmitForCredit, SubmitForCreditHandler>();
        sales.MapCommand<ApproveCredit, ApproveCreditHandler>();
        sales.MapCommand<RejectCredit, RejectCreditHandler>();
        sales.MapCommand<CancelSalesOrder, CancelSalesOrderHandler>();
        sales.MapCommand<CreateQuote, CreateQuoteHandler>();
        sales.MapCommand<UpdateDraftQuote, UpdateDraftQuoteHandler>();
        sales.MapCommand<SubmitQuoteForApproval, SubmitQuoteForApprovalHandler>();
        sales.MapCommand<ApproveQuotePrices, ApproveQuotePricesHandler>();
        sales.MapCommand<ReturnQuoteToDraft, ReturnQuoteToDraftHandler>();
        sales.MapCommand<SendQuote, SendQuoteHandler>();
        sales.MapCommand<MarkQuoteLost, MarkQuoteLostHandler>();
        sales.MapCommand<CancelQuote, CancelQuoteHandler>();
        sales.MapCommand<CopyQuote, CopyQuoteHandler>();
        sales.MapCommand<ConvertQuote, ConvertQuoteHandler>();
        sales.MapCommand<PlanDelivery, PlanDeliveryHandler>();
        sales.MapCommand<StartLoading, StartLoadingHandler>();
        sales.MapCommand<ConfirmLoaded, ConfirmLoadedHandler>();
        sales.MapCommand<RecordGateOut, RecordGateOutHandler>();
        sales.MapCommand<RecordPod, RecordPodHandler>();
        sales.MapCommand<RecordReturnTrip, RecordReturnTripHandler>();
        sales.MapCommand<CancelDelivery, CancelDeliveryHandler>();
        sales.MapCommand<CloseShortSalesOrder, CloseShortSalesOrderHandler>();
        sales.MapCommand<CreateInvoiceFromDeliveries, CreateInvoiceFromDeliveriesHandler>();
        sales.MapCommand<CreateInvoiceFromProformas, CreateInvoiceFromProformasHandler>();
        sales.MapCommand<VoidProforma, VoidProformaHandler>();
        sales.MapCommand<IssueInvoice, IssueInvoiceHandler>();
        sales.MapCommand<RecordExternalFiscalDocument, RecordExternalFiscalDocumentHandler>();
        sales.MapCommand<VoidUnfiscalizedInvoice, VoidUnfiscalizedInvoiceHandler>();
        sales.MapCommand<CreateCreditNote, CreateCreditNoteHandler>();
        sales.MapCommand<IssueCreditNote, IssueCreditNoteHandler>();
        sales.MapCommand<RecordExternalCreditNoteDocument, RecordExternalCreditNoteDocumentHandler>();
        sales.MapCommand<RecordReceipt, RecordReceiptHandler>();
        sales.MapCommand<DepositReceipts, DepositReceiptsHandler>();
        sales.MapCommand<ApplyReceipt, ApplyReceiptHandler>();
        sales.MapCommand<PrepareCustomerRefund, PrepareCustomerRefundHandler>();
        sales.MapCommand<ReleaseCustomerRefund, ReleaseCustomerRefundHandler>();
        sales.MapCommand<VoidCustomerRefund, VoidCustomerRefundHandler>();
        // CF1-02 (E-CF1-1…7, E-CF1-02-1…3): cash sales to the final consumer and their payment.
        sales.MapCommand<CreateCashSale, CreateCashSaleHandler>();
        sales.MapCommand<UpdateCashSaleDraft, UpdateCashSaleDraftHandler>();
        sales.MapCommand<SubmitCashSaleForPayment, SubmitCashSaleForPaymentHandler>();
        sales.MapCommand<ReturnCashSaleToDraft, ReturnCashSaleToDraftHandler>();
        sales.MapCommand<AllocateReceiptToOrder, AllocateReceiptToOrderHandler>();
        sales.MapCommand<ReleaseOrderAllocation, ReleaseOrderAllocationHandler>();
        sales.MapCommand<ConfirmCashSale, ConfirmCashSaleHandler>();
        sales.MapCommand<CancelCashSale, CancelCashSaleHandler>();
        // MAIL-02 (E-MAIL-6, E-MAIL-01-8, 10): documents by e-mail, one permission per document, and the retry of a failed message.
        sales.MapCommand<SendQuoteByEmail, SendQuoteByEmailHandler>();
        sales.MapCommand<SendProformaByEmail, SendProformaByEmailHandler>();
        sales.MapCommand<SendDeliveryByEmail, SendDeliveryByEmailHandler>();
        sales.MapCommand<SendStatementByEmail, SendStatementByEmailHandler>();
        sales.MapCommand<SendArAgingByEmail, SendArAgingByEmailHandler>();
        sales.MapCommand<RetryDocumentEmail, RetryDocumentEmailHandler>();
        sales.MapCommand<AllocateReceiptToProformas, AllocateReceiptToProformasHandler>();
        sales.MapCommand<ReleaseProformaAllocation, ReleaseProformaAllocationHandler>();
        sales.MapCommand<UnapplyReceipt, UnapplyReceiptHandler>();
        sales.MapCommand<MarkReceiptBounced, MarkReceiptBouncedHandler>();
        sales.MapCommand<ReverseReceipt, ReverseReceiptHandler>();
        sales.MapCommand<RecordCustomerWithholding, RecordCustomerWithholdingHandler>();
        sales.MapCommand<ReverseCustomerWithholding, ReverseCustomerWithholdingHandler>();

        var manufacturing = company.MapGroup("/manufacturing").WithTags("Manufacturing");
        manufacturing.MapCommand<CreateMachine, CreateMachineHandler>();
        manufacturing.MapCommand<RenameMachine, RenameMachineHandler>();
        manufacturing.MapCommand<SetMachineStatus, SetMachineStatusHandler>();
        manufacturing.MapCommand<DefineShift, DefineShiftHandler>();
        manufacturing.MapCommand<UpdateShiftTimes, UpdateShiftTimesHandler>();
        manufacturing.MapCommand<SetShiftStatus, SetShiftStatusHandler>();
        manufacturing.MapCommand<PrepareRecipe, PrepareRecipeHandler>();
        manufacturing.MapCommand<ApproveRecipe, ApproveRecipeHandler>();
        manufacturing.MapCommand<StartProductionRun, StartProductionRunHandler>();
        manufacturing.MapCommand<CancelProductionRun, CancelProductionRunHandler>();
        manufacturing.MapCommand<RecordShiftSummary, RecordShiftSummaryHandler>();
        manufacturing.MapCommand<PostShiftSummary, PostShiftSummaryHandler>();
        manufacturing.MapCommand<ReverseShiftSummary, ReverseShiftSummaryHandler>();
        manufacturing.MapCommand<ReleaseLot, ReleaseLotHandler>();
        manufacturing.MapCommand<BlockLot, BlockLotHandler>();
        manufacturing.MapCommand<UnblockLot, UnblockLotHandler>();
        manufacturing.MapCommand<ScrapLot, ScrapLotHandler>();
        manufacturing.MapCommand<SettleCostCollector, SettleCostCollectorHandler>();

        var identity = company.MapGroup("/identity").WithTags("Identity");
        identity.MapCommand<RequestRoleAssignment, RequestRoleAssignmentHandler>();
        identity.MapCommand<RequestRoleRevocation, RequestRoleRevocationHandler>();
        identity.MapCommand<ApproveRoleChange, ApproveRoleChangeHandler>();
        identity.MapCommand<RejectRoleChange, RejectRoleChangeHandler>();
    }

    /// <summary>Every handler class the host resolves (the verifier is built by <see cref="HashVerification"/>).</summary>
    public static IReadOnlyList<Type> Handlers { get; } =
    [
        typeof(UpdateCompanyLegalNameHandler), typeof(UpdatePlantNameHandler),
        typeof(CreateSupplierHandler), typeof(UpdateSupplierHandler), typeof(ActivateSupplierHandler), typeof(SetSupplierPaymentTermsHandler),
        typeof(SetSupplierContactHandler), typeof(ImportSuppliersHandler), typeof(ActivateSuppliersHandler), typeof(CreateRawMaterialHandler), typeof(CreateFinishedGoodHandler), typeof(CreateFreightItemHandler),
        typeof(DefineUomConversionHandler), typeof(ActivateItemHandler),
        typeof(RequestPartyBankAccountHandler), typeof(VerifyPartyBankAccountHandler), typeof(RejectPartyBankAccountHandler),
        typeof(RegisterBankAccountHandler), typeof(CloseBankAccountHandler), typeof(SetBankAccountAliasHandler),
        typeof(PrepareSupplierPaymentHandler), typeof(UpdatePreparedPaymentHandler), typeof(VoidPaymentHandler), typeof(ReleaseSupplierPaymentHandler),
        typeof(ReversePaymentHandler), typeof(ImportBankStatementHandler), typeof(MatchBankLineHandler), typeof(MatchBankLineToReceiptHandler), typeof(MatchBankLineToRefundHandler), typeof(UnmatchBankLineHandler),
        typeof(RecognizeBankChargeHandler),
        typeof(CreatePurchaseOrderHandler), typeof(UpdatePurchaseOrderDraftHandler), typeof(SubmitPurchaseOrderHandler), typeof(ApprovePurchaseOrderHandler),
        typeof(RejectPurchaseOrderHandler), typeof(CancelPurchaseOrderHandler), typeof(ApproveOverReceiptHandler), typeof(PostGoodsReceiptHandler),
        typeof(ReverseGoodsReceiptHandler), typeof(CreateReceiptCorrectionHandler), typeof(ApproveReceiptCorrectionHandler), typeof(RejectReceiptCorrectionHandler),
        typeof(RegisterSupplierInvoiceHandler), typeof(RegisterExpenseInvoiceHandler), typeof(CreateExpensePurchaseOrderHandler), typeof(UpdateExpensePurchaseOrderDraftHandler), typeof(CloseExpensePurchaseOrderHandler), typeof(PrepareExpenseCategoryHandler), typeof(UpdateExpenseCategoryDraftHandler), typeof(ApproveExpenseCategoriesHandler),
        typeof(DeactivateExpenseCategoryHandler), typeof(ReactivateExpenseCategoryHandler), typeof(MatchSupplierInvoiceHandler), typeof(ApproveMatchExceptionHandler), typeof(VoidSupplierInvoiceHandler),
        typeof(PostSupplierInvoiceHandler), typeof(ReverseSupplierInvoiceHandler), typeof(RepostEventHandler), typeof(ApproveValuationResidualAdjustmentHandler),
        typeof(PrepareAccountRoleMapHandler), typeof(ApproveAccountRoleMapHandler), typeof(ApprovePostingRuleVersionHandler), typeof(PrepareAccountingPolicyVersionHandler),
        typeof(ApproveAccountingPolicyVersionHandler),
        typeof(CreateAccountHandler), typeof(UpdateAccountHandler), typeof(DeactivateAccountHandler), typeof(ActivateAccountHandler),
        typeof(PrepareManualJournalHandler), typeof(UpdateManualJournalHandler), typeof(SubmitManualJournalHandler), typeof(WithdrawManualJournalHandler),
        typeof(ApproveManualJournalHandler), typeof(RejectManualJournalHandler), typeof(ReverseManualJournalHandler),
        typeof(PrepareReportStructureHandler), typeof(ApproveReportStructureHandler),
        typeof(RegisterFiscalSourceHandler), typeof(ConfigureFiscalRuleVersionHandler), typeof(LinkFiscalSourceHandler), typeof(RunFiscalRuleTestsHandler),
        typeof(ActivateFiscalRuleVersionHandler),
        typeof(RegisterFiscalAuthorizationHandler), typeof(UpdateDraftAuthorizationHandler), typeof(AttachAuthorizationDocumentHandler), typeof(SubmitForVerificationHandler), typeof(VerifyAuthorizationHandler), typeof(ReturnAuthorizationToDraftHandler), typeof(RejectAuthorizationHandler), typeof(SuspendAuthorizationHandler), typeof(ReactivateAuthorizationHandler), typeof(ExpireFiscalAuthorizationsHandler),
        typeof(RunReconciliationHandler), typeof(CloseComponentHandler), typeof(RequestReopenHandler), typeof(ApproveReopenHandler), typeof(RejectReopenHandler),
        typeof(CreateCustomerHandler), typeof(UpdateCustomerHandler), typeof(ActivateCustomerHandler), typeof(PrepareCustomerTermsHandler), typeof(ApproveCustomerTermsHandler),
        typeof(ImportCustomersHandler), typeof(ApproveCustomerTermsBatchHandler), typeof(ActivateCustomersHandler), typeof(PrepareStandardCostHandler), typeof(PrepareStandardCostFromRecipeHandler), typeof(ApproveStandardCostHandler), typeof(PreparePriceListHandler), typeof(ApprovePriceListHandler), typeof(CreatePriceListHandler), typeof(DeactivatePriceListHandler), typeof(ReactivatePriceListHandler), typeof(CreateDeliveryZoneHandler), typeof(RenameDeliveryZoneHandler), typeof(DeactivateDeliveryZoneHandler), typeof(ReactivateDeliveryZoneHandler),
        typeof(RegisterVehicleHandler), typeof(UpdateVehicleHandler), typeof(DeactivateVehicleHandler), typeof(ActivateVehicleHandler), typeof(RegisterDriverHandler), typeof(UpdateDriverHandler), typeof(DeactivateDriverHandler), typeof(ActivateDriverHandler),
        typeof(PrepareOpeningInventoryHandler), typeof(PostOpeningInventoryHandler), typeof(ReverseOpeningInventoryHandler),
        typeof(CreateSalesOrderHandler), typeof(UpdateSalesOrderDraftHandler), typeof(SubmitForCreditHandler), typeof(ApproveCreditHandler), typeof(RejectCreditHandler), typeof(CancelSalesOrderHandler),
        typeof(CreateQuoteHandler), typeof(UpdateDraftQuoteHandler), typeof(SubmitQuoteForApprovalHandler), typeof(ApproveQuotePricesHandler), typeof(ReturnQuoteToDraftHandler),
        typeof(SendQuoteHandler), typeof(MarkQuoteLostHandler), typeof(CancelQuoteHandler), typeof(CopyQuoteHandler), typeof(ConvertQuoteHandler),
        typeof(PlanDeliveryHandler), typeof(StartLoadingHandler), typeof(ConfirmLoadedHandler), typeof(RecordGateOutHandler), typeof(RecordPodHandler), typeof(RecordReturnTripHandler), typeof(CancelDeliveryHandler), typeof(CloseShortSalesOrderHandler),
        typeof(CreateInvoiceFromDeliveriesHandler), typeof(CreateInvoiceFromProformasHandler), typeof(VoidProformaHandler), typeof(IssueInvoiceHandler), typeof(RecordExternalFiscalDocumentHandler), typeof(VoidUnfiscalizedInvoiceHandler),
        typeof(CreateCreditNoteHandler), typeof(IssueCreditNoteHandler), typeof(RecordExternalCreditNoteDocumentHandler),
        typeof(RecordReceiptHandler), typeof(DepositReceiptsHandler), typeof(ApplyReceiptHandler), typeof(PrepareCustomerRefundHandler), typeof(ReleaseCustomerRefundHandler), typeof(VoidCustomerRefundHandler),
        typeof(SendQuoteByEmailHandler), typeof(SendProformaByEmailHandler), typeof(SendDeliveryByEmailHandler), typeof(SendStatementByEmailHandler), typeof(SendArAgingByEmailHandler),
        typeof(RetryDocumentEmailHandler),
        typeof(CreateCashSaleHandler), typeof(UpdateCashSaleDraftHandler), typeof(SubmitCashSaleForPaymentHandler), typeof(ReturnCashSaleToDraftHandler), typeof(AllocateReceiptToOrderHandler),
        typeof(ReleaseOrderAllocationHandler), typeof(ConfirmCashSaleHandler), typeof(CancelCashSaleHandler),
        typeof(AllocateReceiptToProformasHandler), typeof(ReleaseProformaAllocationHandler), typeof(UnapplyReceiptHandler), typeof(MarkReceiptBouncedHandler),
        typeof(ReverseReceiptHandler), typeof(RecordCustomerWithholdingHandler), typeof(ReverseCustomerWithholdingHandler),
        typeof(CreateMachineHandler), typeof(RenameMachineHandler), typeof(SetMachineStatusHandler), typeof(DefineShiftHandler), typeof(UpdateShiftTimesHandler), typeof(SetShiftStatusHandler),
        typeof(PrepareRecipeHandler), typeof(ApproveRecipeHandler),
        typeof(StartProductionRunHandler), typeof(CancelProductionRunHandler), typeof(RecordShiftSummaryHandler), typeof(PostShiftSummaryHandler), typeof(ReverseShiftSummaryHandler),
        typeof(ReleaseLotHandler), typeof(BlockLotHandler), typeof(UnblockLotHandler), typeof(ScrapLotHandler), typeof(SettleCostCollectorHandler),
        typeof(RequestRoleAssignmentHandler), typeof(RequestRoleRevocationHandler), typeof(ApproveRoleChangeHandler), typeof(RejectRoleChangeHandler),
    ];

    /// <summary>"CreatePurchaseOrder" → "create-purchase-order".</summary>
    public static string Kebab(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    private static void MapCommand<TCommand, THandler>(this RouteGroupBuilder group)
        where TCommand : ICommand
        where THandler : class, ICommandHandler<TCommand>
        => group.MapPost("/" + Kebab(typeof(TCommand).Name), (HttpContext http, Guid companyId, THandler handler, CommandRunner runner, CancellationToken cancellationToken)
                => runner.RunAsync<TCommand>(http, companyId, handler, cancellationToken))
            .Describe<TCommand>();

    private static RouteHandlerBuilder Describe<TCommand>(this RouteHandlerBuilder endpoint)
        where TCommand : ICommand
        => endpoint
            .WithName(typeof(TCommand).Name)
            .Accepts<TCommand>("application/json")
            .Produces<CommandResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithMetadata(new CommandEndpointMetadata(typeof(TCommand)));
}

/// <summary>Marks a command endpoint (OpenAPI adds the Idempotency-Key header; tests check every handler is exposed).</summary>
public sealed record CommandEndpointMetadata(Type CommandType);
