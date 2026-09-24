using System.Text;
using Rochell.Api.Hosting;
using Rochell.Api.Http;
using Rochell.Audit;
using Rochell.Finance.Configuration;
using Rochell.Finance.Policies;
using Rochell.Identity.RoleChanges;
using Rochell.MasterData.Items;
using Rochell.MasterData.Suppliers;
using Rochell.Platform.Commands;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.Ledger;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation;
using Rochell.Tax;

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
        masterData.MapCommand<CreateSupplier, CreateSupplierHandler>();
        masterData.MapCommand<UpdateSupplier, UpdateSupplierHandler>();
        masterData.MapCommand<ActivateSupplier, ActivateSupplierHandler>();
        masterData.MapCommand<CreateRawMaterial, CreateRawMaterialHandler>();
        masterData.MapCommand<DefineUomConversion, DefineUomConversionHandler>();
        masterData.MapCommand<ActivateItem, ActivateItemHandler>();

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
        procurement.MapCommand<MatchSupplierInvoice, MatchSupplierInvoiceHandler>();
        procurement.MapCommand<ApproveMatchException, ApproveMatchExceptionHandler>();
        procurement.MapCommand<VoidSupplierInvoice, VoidSupplierInvoiceHandler>();
        procurement.MapCommand<PostSupplierInvoice, PostSupplierInvoiceHandler>();
        procurement.MapCommand<ReverseSupplierInvoice, ReverseSupplierInvoiceHandler>();
        procurement.MapCommand<RepostEvent, RepostEventHandler>();
        procurement.MapCommand<ApproveValuationResidualAdjustment, ApproveValuationResidualAdjustmentHandler>();

        var finance = company.MapGroup("/finance").WithTags("Finance");
        finance.MapCommand<ApproveAccountRoleMap, ApproveAccountRoleMapHandler>();
        finance.MapCommand<ApprovePostingRuleVersion, ApprovePostingRuleVersionHandler>();
        finance.MapCommand<PrepareAccountingPolicyVersion, PrepareAccountingPolicyVersionHandler>();
        finance.MapCommand<ApproveAccountingPolicyVersion, ApproveAccountingPolicyVersionHandler>();

        var tax = company.MapGroup("/tax").WithTags("Tax");
        tax.MapCommand<RegisterFiscalSource, RegisterFiscalSourceHandler>();
        tax.MapCommand<ConfigureFiscalRuleVersion, ConfigureFiscalRuleVersionHandler>();
        tax.MapCommand<LinkFiscalSource, LinkFiscalSourceHandler>();
        tax.MapCommand<RunFiscalRuleTests, RunFiscalRuleTestsHandler>();
        tax.MapCommand<ActivateFiscalRuleVersion, ActivateFiscalRuleVersionHandler>();

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

        var identity = company.MapGroup("/identity").WithTags("Identity");
        identity.MapCommand<RequestRoleAssignment, RequestRoleAssignmentHandler>();
        identity.MapCommand<RequestRoleRevocation, RequestRoleRevocationHandler>();
        identity.MapCommand<ApproveRoleChange, ApproveRoleChangeHandler>();
    }

    /// <summary>Every handler class the host resolves (the verifier is built by <see cref="HashVerification"/>).</summary>
    public static IReadOnlyList<Type> Handlers { get; } =
    [
        typeof(CreateSupplierHandler), typeof(UpdateSupplierHandler), typeof(ActivateSupplierHandler), typeof(CreateRawMaterialHandler),
        typeof(DefineUomConversionHandler), typeof(ActivateItemHandler),
        typeof(CreatePurchaseOrderHandler), typeof(UpdatePurchaseOrderDraftHandler), typeof(SubmitPurchaseOrderHandler), typeof(ApprovePurchaseOrderHandler),
        typeof(RejectPurchaseOrderHandler), typeof(CancelPurchaseOrderHandler), typeof(ApproveOverReceiptHandler), typeof(PostGoodsReceiptHandler),
        typeof(ReverseGoodsReceiptHandler), typeof(CreateReceiptCorrectionHandler), typeof(ApproveReceiptCorrectionHandler), typeof(RejectReceiptCorrectionHandler),
        typeof(RegisterSupplierInvoiceHandler), typeof(MatchSupplierInvoiceHandler), typeof(ApproveMatchExceptionHandler), typeof(VoidSupplierInvoiceHandler),
        typeof(PostSupplierInvoiceHandler), typeof(ReverseSupplierInvoiceHandler), typeof(RepostEventHandler), typeof(ApproveValuationResidualAdjustmentHandler),
        typeof(ApproveAccountRoleMapHandler), typeof(ApprovePostingRuleVersionHandler), typeof(PrepareAccountingPolicyVersionHandler),
        typeof(ApproveAccountingPolicyVersionHandler),
        typeof(RegisterFiscalSourceHandler), typeof(ConfigureFiscalRuleVersionHandler), typeof(LinkFiscalSourceHandler), typeof(RunFiscalRuleTestsHandler),
        typeof(ActivateFiscalRuleVersionHandler),
        typeof(RunReconciliationHandler), typeof(CloseComponentHandler), typeof(RequestReopenHandler), typeof(ApproveReopenHandler), typeof(RejectReopenHandler),
        typeof(RequestRoleAssignmentHandler), typeof(RequestRoleRevocationHandler), typeof(ApproveRoleChangeHandler),
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
