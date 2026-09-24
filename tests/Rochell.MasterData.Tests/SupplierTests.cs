using Rochell.MasterData.Suppliers;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>Suppliers: create (DRAFT), update only in DRAFT, activation by the Controller with step-up (E-PR04-3…6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierTests(PostgresFixture postgres)
{
    private static Task<CommandResult> Create(TestHarness h, Guid session, string key, string rnc, string name = "Cementos del Este, S.R.L.")
        => h.RunAsync(new CreateSupplier(h.CompanyId, session, key, rnc, name), new CreateSupplierHandler());

    [Fact]
    public async Task Supplier_is_created_in_draft_with_normalized_rnc()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var result = await Create(h, buyer, "s-1", "1-01-12345-6");

        Assert.True(await h.ScalarAsync<bool>(
            "SELECT rnc = '101123456' AND party_kind = 'LOCAL' AND is_supplier AND status = 'DRAFT' AND version = 1 AND rnc_validated_at IS NULL FROM md.party WHERE party_id = @p",
            ("p", result.ResultRef)));
        Assert.Equal("SupplierCreated", await h.ScalarAsync<string>("SELECT event_type FROM core.domain_event WHERE aggregate_id = @p", ("p", result.ResultRef)));
    }

    [Theory]
    [InlineData("10112345")]      // 8 digits
    [InlineData("1011234567")]    // 10 digits
    [InlineData("10A123456")]     // letters
    [InlineData("")]
    public async Task Invalid_rnc_formats_are_rejected(string rnc)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Create(h, buyer, "bad-" + rnc, rnc));

        Assert.Equal(MasterDataErrors.RncInvalid, ex.Code);
    }

    [Fact]
    public async Task Cedula_with_eleven_digits_is_accepted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var result = await Create(h, buyer, "cedula", "001-1234567-8", "Juan Pérez (transportista)");

        Assert.Equal("00112345678", await h.ScalarAsync<string>("SELECT rnc FROM md.party WHERE party_id = @p", ("p", result.ResultRef)));
    }

    [Fact]
    public async Task Duplicate_rnc_in_the_same_company_is_rejected_but_allowed_in_another()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        await Create(h, buyer, "dup-1", "101000011");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Create(h, buyer, "dup-2", "101-00001-1"));
        var otherCompany = await h.CreateCompanyAsync();
        Assert.Null(await h.AdminExecuteAsync(
            $"INSERT INTO md.party VALUES (gen_random_uuid(), '{otherCompany}', 'LOCAL', '101000011', 'Otra', true, 'DRAFT', NULL, 1)"));

        Assert.Equal(MasterDataErrors.RncDuplicate, ex.Code);
    }

    [Fact]
    public async Task Update_requires_draft_and_the_expected_version()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var party = (await Create(h, buyer, "u-1", "101000021")).ResultRef;

        await h.RunAsync(new UpdateSupplier(h.CompanyId, buyer, "u-2", party, 1, "101000021", "Nombre corregido"), new UpdateSupplierHandler());
        var stale = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new UpdateSupplier(h.CompanyId, buyer, "u-3", party, 1, "101000021", "Otra vez"), new UpdateSupplierHandler()));
        await h.RunAsync(new ActivateSupplier(h.CompanyId, controller, "u-4", party, 2), new ActivateSupplierHandler());
        var afterActivation = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new UpdateSupplier(h.CompanyId, buyer, "u-5", party, 3, "101000021", "Cambio tardío"), new UpdateSupplierHandler()));

        Assert.Equal(MasterDataErrors.VersionConflict, stale.Code);
        Assert.Equal(MasterDataErrors.NotDraft, afterActivation.Code);
        Assert.Equal("Nombre corregido", await h.ScalarAsync<string>("SELECT legal_name FROM md.party WHERE party_id = @p", ("p", party)));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT version FROM md.party WHERE party_id = @p", ("p", party)));
    }

    [Fact]
    public async Task Buyer_cannot_activate_and_controller_activation_is_published()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var party = (await Create(h, buyer, "a-1", "101000031")).ResultRef;

        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new ActivateSupplier(h.CompanyId, buyer, "a-2", party, 1), new ActivateSupplierHandler()));
        await h.RunAsync(new ActivateSupplier(h.CompanyId, controller, "a-3", party, 1), new ActivateSupplierHandler());

        Assert.Equal(AuthorizationErrors.NotAuthorized, denied.Code);
        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status::text FROM md.party WHERE party_id = @p", ("p", party)));
        Assert.Equal(1L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM core.outbox o JOIN core.domain_event e USING (company_id, event_id) WHERE e.event_type = 'SupplierActivated' AND e.aggregate_id = @p",
            ("p", party)));
    }

    [Fact]
    public async Task Activation_requires_recent_re_authentication()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var party = (await Create(h, buyer, "r-1", "101000041")).ResultRef;
        clock.Advance(TimeSpan.FromMinutes(6));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new ActivateSupplier(h.CompanyId, controller, "r-2", party, 1), new ActivateSupplierHandler()));

        Assert.Equal(AuthorizationErrors.StepUpRequired, ex.Code);
    }
}
