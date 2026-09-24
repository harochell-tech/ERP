using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>Purchase order state machine (§11.1), SC-01, approval limits and step-up (E-PR08-2), over-receipt (K-13).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PurchaseOrderTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static PurchaseOrderLineInput[] Lines(TestPurchasing p, decimal qty = 20m, decimal price = 1250.50m)
        => [new(p.Sand, "t", qty, price), new(p.Cement, "t", 5m, 7800m)];

    private static async Task<Guid> Create(TestHarness h, TestPurchasing p, string key, PurchaseOrderLineInput[]? lines = null, Guid? session = null)
    {
        var result = await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, session ?? p.Buyer, key, p.PlantId, p.SupplierId, Today(h), lines ?? Lines(p)), new CreatePurchaseOrderHandler());
        return result.ResultRef;
    }

    private static Task<CommandResult> Submit(TestHarness h, TestPurchasing p, Guid po, long version, string key)
        => h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, key, p.PlantId, po, version), new SubmitPurchaseOrderHandler());

    private static Task<CommandResult> Approve(TestHarness h, TestPurchasing p, Guid po, long version, string key, Guid? session = null)
        => h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, session ?? p.Approver, key, p.PlantId, po, version), new ApprovePurchaseOrderHandler());

    private static Task<string?> Status(TestHarness h, Guid po)
        => h.ScalarAsync<string>("SELECT status::text || ':' || version FROM pur.purchase_order WHERE po_id = @p", ("p", po));

    [Fact]
    public async Task Create_submit_approve_copies_tolerance_and_policy()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();

        var po = await Create(h, p, "po-1");
        await Submit(h, p, po, 1, "po-1-s");
        await Approve(h, p, po, 2, "po-1-a");

        Assert.Equal("APPROVED:3", await Status(h, po));
        Assert.Matches("^OC-[0-9]{4}-[0-9A-F]{8}$", (await h.ScalarAsync<string>("SELECT po_no FROM pur.purchase_order WHERE po_id = @p", ("p", po)))!);
        Assert.Equal("0.020000;0.020000", await h.ScalarAsync<string>("SELECT string_agg(receipt_tolerance_pct::text, ';' ORDER BY line_no) FROM pur.purchase_order_line WHERE po_id = @p", ("p", po)));
        Assert.Equal(p.PolicyVersionId, await h.ScalarAsync<Guid>("SELECT policy_version_id FROM pur.purchase_order WHERE po_id = @p", ("p", po)));
        Assert.Equal("->DRAFT,DRAFT->PENDING_APPROVAL,PENDING_APPROVAL->APPROVED", await h.ScalarAsync<string>(
            "SELECT string_agg(coalesce(from_state, '') || '->' || to_state, ',' ORDER BY to_state = 'DRAFT' DESC, to_state = 'PENDING_APPROVAL' DESC) FROM core.state_history WHERE aggregate_id = @p",
            ("p", po)));
    }

    [Fact]
    public async Task SC01_creator_cannot_approve_even_holding_the_approver_role()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var buyerApprover = await h.SessionWithRolesAsync("COMPRADOR", "APROBADOR_COMPRAS");
        var po = await Create(h, p, "sc01", session: buyerApprover);
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, buyerApprover, "sc01-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());

        var ex = await Assert.ThrowsAsync<DomainException>(() => Approve(h, p, po, 2, "sc01-a", buyerApprover));

        Assert.Equal(ProcurementErrors.ApproverIsCreator, ex.Code);
        Assert.Equal("PENDING_APPROVAL:2", await Status(h, po));
    }

    [Fact]
    public async Task Purchasing_approver_is_limited_and_the_controller_is_not()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var po = await Create(h, p, "big", [new(p.Cement, "t", 20m, 7800m)]); // 156,000 > 100,000
        await Submit(h, p, po, 1, "big-s");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Approve(h, p, po, 2, "big-a"));
        await Approve(h, p, po, 2, "big-c", p.Controller);

        Assert.Equal(ProcurementErrors.ApprovalLimitExceeded, ex.Code);
        Assert.Equal("APPROVED:3", await Status(h, po));
    }

    [Fact]
    public async Task Approving_above_the_step_up_threshold_requires_re_authentication()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var p = await h.CreatePurchasingSetupAsync();
        var small = await Create(h, p, "small", [new(p.Sand, "t", 10m, 1000m)]);     // 10,000 < 50,000
        var large = await Create(h, p, "large", [new(p.Cement, "t", 10m, 7800m)]);  // 78,000 > 50,000
        await Submit(h, p, small, 1, "small-s");
        await Submit(h, p, large, 1, "large-s");
        clock.Advance(TimeSpan.FromMinutes(6));

        await Approve(h, p, small, 2, "small-a");
        var ex = await Assert.ThrowsAsync<DomainException>(() => Approve(h, p, large, 2, "large-a"));

        Assert.Equal(AuthorizationErrors.StepUpRequired, ex.Code);
        Assert.Equal("APPROVED:3", await Status(h, small));
        Assert.Equal("PENDING_APPROVAL:2", await Status(h, large));
    }

    [Fact]
    public async Task Reject_returns_to_draft_with_reason_and_the_draft_can_be_edited_and_resubmitted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var po = await Create(h, p, "rej");
        await Submit(h, p, po, 1, "rej-s");

        await h.RunAsync(new RejectPurchaseOrder(h.CompanyId, p.Approver, "rej-r", p.PlantId, po, 2, "Precio fuera de mercado"), new RejectPurchaseOrderHandler());
        await h.RunAsync(new UpdatePurchaseOrderDraft(h.CompanyId, p.Buyer, "rej-u", p.PlantId, po, 3, [new(p.Sand, "m3", 12m, 1800m)]), new UpdatePurchaseOrderDraftHandler());
        await Submit(h, p, po, 4, "rej-s2");
        await Approve(h, p, po, 5, "rej-a");

        Assert.Equal("APPROVED:6", await Status(h, po));
        Assert.Equal("Precio fuera de mercado", await h.ScalarAsync<string>("SELECT reason FROM core.state_history WHERE aggregate_id = @p AND from_state = 'PENDING_APPROVAL' AND to_state = 'DRAFT'", ("p", po)));
        Assert.Equal("m3:12.000000", await h.ScalarAsync<string>("SELECT string_agg(uom || ':' || qty_ordered, ',') FROM pur.purchase_order_line WHERE po_id = @p", ("p", po)));
    }

    public static TheoryData<string, string> InvalidOrders() => new()
    {
        { "zero-price", ProcurementErrors.PriceInvalid },
        { "no-lines", ProcurementErrors.LinesRequired },
        { "uom-without-conversion", ProcurementErrors.UomNotConvertible },
        { "draft-item", ProcurementErrors.ItemNotActive },
        { "draft-supplier", ProcurementErrors.SupplierNotActive },
        { "too-many-decimals", ProcurementErrors.QuantityInvalid },
    };

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public async Task Invalid_orders_are_rejected(string @case, string expected)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var draftItem = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{draftItem}', '{h.CompanyId}', 'ITEM-DRAFT', 'Borrador', 'RAW_MATERIAL', 't', 'ADITIVO', 'DRAFT', 1)");
        var draftSupplier = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.party VALUES ('{draftSupplier}', '{h.CompanyId}', 'LOCAL', '101000099', 'Borrador', true, 'DRAFT', NULL, 1)");

        PurchaseOrderLineInput[] lines = @case switch
        {
            "zero-price" => [new(p.Sand, "t", 1m, 0m)],
            "no-lines" => [],
            "uom-without-conversion" => [new(p.Cement, "m3", 1m, 10m)],
            "draft-item" => [new(draftItem, "t", 1m, 10m)],
            "too-many-decimals" => [new(p.Sand, "t", 1.0000001m, 10m)],
            _ => [new(p.Sand, "t", 1m, 10m)],
        };
        var supplier = @case == "draft-supplier" ? draftSupplier : p.SupplierId;

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "bad-" + @case, p.PlantId, supplier, Today(h), lines), new CreatePurchaseOrderHandler()));

        Assert.Equal(expected, ex.Code);
        Assert.Equal(0L, await h.CountAsync("pur.purchase_order"));
    }

    [Fact]
    public async Task Invalid_transitions_version_conflicts_and_plant_mismatch_are_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var otherPlant = await h.CreatePlantAsync();
        var po = await Create(h, p, "tr");

        var approveDraft = await Assert.ThrowsAsync<DomainException>(() => Approve(h, p, po, 1, "tr-a", p.Controller));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Submit(h, p, po, 7, "tr-s"));
        var wrongPlant = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "tr-p", otherPlant, po, 1), new SubmitPurchaseOrderHandler()));

        Assert.Equal(ProcurementErrors.InvalidState, approveDraft.Code);
        Assert.Equal(ProcurementErrors.VersionConflict, stale.Code);
        Assert.Equal(ProcurementErrors.PlantMismatch, wrongPlant.Code);
    }

    [Fact]
    public async Task Cancel_is_allowed_before_any_receipt_only()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var draft = await Create(h, p, "c-draft");
        var approved = await Create(h, p, "c-approved");
        await Submit(h, p, approved, 1, "c-s");
        await Approve(h, p, approved, 2, "c-a");
        var received = await Create(h, p, "c-received");
        await Submit(h, p, received, 1, "c-s2");
        await Approve(h, p, received, 2, "c-a2");
        await h.AdminRequireAsync($"UPDATE pur.purchase_order_line SET qty_received = 1, version = version + 1 WHERE po_id = '{received}' AND line_no = 1");

        await h.RunAsync(new CancelPurchaseOrder(h.CompanyId, p.Buyer, "c-1", p.PlantId, draft, 1, "Ya no se necesita"), new CancelPurchaseOrderHandler());
        await h.RunAsync(new CancelPurchaseOrder(h.CompanyId, p.Buyer, "c-2", p.PlantId, approved, 3, "Proveedor sin disponibilidad"), new CancelPurchaseOrderHandler());
        var withReceipt = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new CancelPurchaseOrder(h.CompanyId, p.Buyer, "c-3", p.PlantId, received, 3, "x"), new CancelPurchaseOrderHandler()));
        var again = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new CancelPurchaseOrder(h.CompanyId, p.Buyer, "c-4", p.PlantId, draft, 2, "x"), new CancelPurchaseOrderHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new CancelPurchaseOrder(h.CompanyId, p.Buyer, "c-5", p.PlantId, received, 3, " "), new CancelPurchaseOrderHandler()));

        Assert.Equal("CANCELLED:2", await Status(h, draft));
        Assert.Equal("CANCELLED:4", await Status(h, approved));
        Assert.Equal(ProcurementErrors.AlreadyReceived, withReceipt.Code);
        Assert.Equal(ProcurementErrors.InvalidState, again.Code);
        Assert.Equal(ProcurementErrors.ReasonRequired, noReason.Code);
    }

    [Fact]
    public async Task Over_receipt_approval_raises_the_line_limit_and_requires_step_up()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var p = await h.CreatePurchasingSetupAsync();
        var po = await Create(h, p, "or");
        await Submit(h, p, po, 1, "or-s");
        await Approve(h, p, po, 2, "or-a");
        var line = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p AND line_no = 1", ("p", po));

        await h.RunAsync(new ApproveOverReceipt(h.CompanyId, p.Approver, "or-1", p.PlantId, po, line, 3m, "Camión con carga completa"), new ApproveOverReceiptHandler());
        var fits = await h.AdminExecuteAsync($"UPDATE pur.purchase_order_line SET qty_received = 23.4, version = version + 1 WHERE po_line_id = '{line}'"); // 20 × 1.02 + 3
        var exceeds = await h.AdminExecuteAsync($"UPDATE pur.purchase_order_line SET qty_received = 23.41, version = version + 1 WHERE po_line_id = '{line}'");
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new ApproveOverReceipt(h.CompanyId, p.Approver, "or-2", p.PlantId, po, line, 1m, "x"), new ApproveOverReceiptHandler()));

        Assert.Null(fits);
        Assert.Equal("23514", exceeds?.SqlState);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
    }

    [Fact]
    public async Task Buyer_cannot_approve_and_approver_cannot_create()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();

        var create = await Assert.ThrowsAsync<DomainException>(() => Create(h, p, "perm-c", session: p.Approver));
        var po = await Create(h, p, "perm");
        await Submit(h, p, po, 1, "perm-s");
        var approve = await Assert.ThrowsAsync<DomainException>(() => Approve(h, p, po, 2, "perm-a", p.Buyer));

        Assert.Equal(AuthorizationErrors.NotAuthorized, create.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, approve.Code);
    }

    [Fact]
    public async Task Duplicate_create_returns_the_same_order()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();

        var first = await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "dup", p.PlantId, p.SupplierId, Today(h), Lines(p)), new CreatePurchaseOrderHandler());
        var second = await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "dup", p.PlantId, p.SupplierId, Today(h), Lines(p)), new CreatePurchaseOrderHandler());

        Assert.True(second.Duplicate);
        Assert.Equal(first.ResultRef, second.ResultRef);
        Assert.Equal(1L, await h.CountAsync("pur.purchase_order"));
        Assert.Equal(JsonDocument.Parse(first.ResultPayload).RootElement.GetProperty("poNo").GetString(), JsonDocument.Parse(second.ResultPayload).RootElement.GetProperty("poNo").GetString());
    }
}
