using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Orders;
using Rochell.Tax;
using Rochell.Tax.Authorizations;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1-02: registration, documents, verification, suspension of fiscal authorizations and the order's proforma (E-FIS1-02-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalAuthorizationTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private sealed record World(DeliveryTests.Setup S, Guid Billing, Guid Specialist, Guid Order);

    private static async Task<World> WorldAsync(TestHarness h)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        var (order, _) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m);
        return new World(s, await h.SessionWithRolesAsync("FACTURACION"), await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"), order);
    }

    private static RegisterFiscalAuthorization Register(TestHarness h, World w, string key, string certificate = "CERT-2026-0001", decimal quantity = 1000m, decimal net = 50000.00m, DateOnly? validUntil = null)
        => new(h.CompanyId, w.Billing, key, w.S.Customer, certificate, new DateOnly(2026, 9, 1), validUntil ?? new DateOnly(2027, 3, 1), "Hotel Playa Bávaro",
            "CONFOTUR-0456-2025", new DateOnly(2040, 12, 31), w.Order, [new AuthorizationLineInput(w.S.Block, "un", quantity, net)]);

    private static Task<CommandResult> Attach(TestHarness h, World w, Guid authorization, string key, string kind = "CERTIFICADO_DGII")
        => h.RunAsync(new AttachAuthorizationDocument(h.CompanyId, w.Billing, key, authorization, kind, "certificado-dgii.pdf", DeliveryTests.Hash), new AttachAuthorizationDocumentHandler());

    [Trait("AcceptanceFis1", "FIS-01")]
    [Fact]
    public async Task FIS01_an_authorization_is_registered_with_its_certificate_and_verified_by_the_fiscal_specialist()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        var authorization = (await h.RunAsync(Register(h, w, "reg"), new RegisterFiscalAuthorizationHandler())).ResultRef;
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register(h, w, "reg-2"), new RegisterFiscalAuthorizationHandler()));
        var noCertificate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SubmitForVerification(h.CompanyId, w.Billing, "sub-0", authorization, 1), new SubmitForVerificationHandler()));
        await h.RunAsync(
            new UpdateDraftAuthorization(h.CompanyId, w.Billing, "upd", authorization, 1, "CERT-2026-0001", new DateOnly(2026, 9, 1), new DateOnly(2027, 3, 1), "Hotel Playa Bávaro",
                "CONFOTUR-0456-2025", null, w.Order, [new AuthorizationLineInput(w.S.Block, "un", 1200m, 60000.00m)]),
            new UpdateDraftAuthorizationHandler());
        await Attach(h, w, authorization, "doc");
        await Attach(h, w, authorization, "doc-2", "RESOLUCION_CONFOTUR");
        await h.RunAsync(new SubmitForVerification(h.CompanyId, w.Billing, "sub", authorization, 2), new SubmitForVerificationHandler());
        var billingVerifies = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VerifyAuthorization(h.CompanyId, w.Billing, "ver-0", authorization, 3), new VerifyAuthorizationHandler()));
        await h.RunAsync(new ReturnAuthorizationToDraft(h.CompanyId, w.Specialist, "ret", authorization, 3, "Falta la lista de materiales"), new ReturnAuthorizationToDraftHandler());
        await Attach(h, w, authorization, "doc-3", "LISTA_MATERIALES");
        await h.RunAsync(new SubmitForVerification(h.CompanyId, w.Billing, "sub-2", authorization, 4), new SubmitForVerificationHandler());
        await h.RunAsync(new VerifyAuthorization(h.CompanyId, w.Specialist, "ver", authorization, 5), new VerifyAuthorizationHandler());
        var suspendNoReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SuspendAuthorization(h.CompanyId, w.Specialist, "sus-0", authorization, 6, " "), new SuspendAuthorizationHandler()));
        await h.RunAsync(new SuspendAuthorization(h.CompanyId, w.Specialist, "sus", authorization, 6, "Revisión de la DGII"), new SuspendAuthorizationHandler());
        await h.RunAsync(new ReactivateAuthorization(h.CompanyId, w.Specialist, "rea", authorization, 7, "Confirmada"), new ReactivateAuthorizationHandler());

        Assert.Equal(
            (TaxErrors.AuthorizationCertificateDuplicate, TaxErrors.AuthorizationCertificateRequired, AuthorizationErrors.NotAuthorized, TaxErrors.AuthorizationReasonRequired),
            (duplicate.Code, noCertificate.Code, billingVerifies.Code, suspendNoReason.Code));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetFiscalAuthorization(h.CompanyId, w.S.Seller, authorization), new GetFiscalAuthorizationHandler())).RootElement;
        Assert.Equal("ACTIVE|8|60000.0000|0.0000", $"{detail.GetProperty("header").GetProperty("status").GetString()}|{detail.GetProperty("header").GetProperty("version").GetInt64()}|" +
            $"{detail.GetProperty("header").GetProperty("netAuthorized").GetString()}|{detail.GetProperty("header").GetProperty("netConsumed").GetString()}");
        Assert.Equal("1200.000000:60000.0000", string.Join('|', detail.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("qtyAvailable").GetString()}:{l.GetProperty("netAvailable").GetString()}")));
        Assert.Equal(3, detail.GetProperty("documents").GetArrayLength());
        Assert.Equal("DRAFT,PENDING_VERIFICATION,DRAFT,PENDING_VERIFICATION,ACTIVE,SUSPENDED,ACTIVE", string.Join(',', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));
        Assert.NotNull(detail.GetProperty("verifiedBy").GetString());
    }

    [Fact]
    public async Task Registration_checks_the_customer_the_products_and_the_amounts_and_an_expired_certificate_is_not_verified()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await WorldAsync(h);
        var paver = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{paver}', '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'DRAFT', 1)");
        var supplier = await h.CreateActiveSupplierAsync("101000001", "Solo proveedor");

        var notCustomer = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register(h, w, "sup") with { PartyId = supplier }, new RegisterFiscalAuthorizationHandler()));
        var inactiveItem = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            Register(h, w, "item") with { Lines = [new AuthorizationLineInput(paver, "un", 10m, 500.00m)] }, new RegisterFiscalAuthorizationHandler()));
        var precise = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register(h, w, "net", net: 10.001m), new RegisterFiscalAuthorizationHandler()));
        var noLines = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register(h, w, "none") with { Lines = [] }, new RegisterFiscalAuthorizationHandler()));
        var shortLived = (await h.RunAsync(Register(h, w, "short", "CERT-SHORT", validUntil: BusinessCalendarToday(clock).AddDays(1)), new RegisterFiscalAuthorizationHandler())).ResultRef;
        await Attach(h, w, shortLived, "doc");
        await h.RunAsync(new SubmitForVerification(h.CompanyId, w.Billing, "sub", shortLived, 1), new SubmitForVerificationHandler());
        clock.Advance(TimeSpan.FromDays(3));
        var specialist = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");
        var expired = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VerifyAuthorization(h.CompanyId, specialist, "ver", shortLived, 2), new VerifyAuthorizationHandler()));
        await h.RunAsync(new RejectAuthorization(h.CompanyId, specialist, "rej", shortLived, 2, "Certificado vencido"), new RejectAuthorizationHandler());

        Assert.Equal(
            (TaxErrors.AuthorizationCustomerInvalid, TaxErrors.AuthorizationItemInvalid, TaxErrors.AuthorizationFieldInvalid, TaxErrors.AuthorizationLinesRequired, TaxErrors.AuthorizationExpired),
            (notCustomer.Code, inactiveItem.Code, precise.Code, noLines.Code, expired.Code));
        Assert.Equal("REJECTED", await h.ScalarAsync<string>("SELECT status FROM tax.fiscal_authorization WHERE authorization_id = @a", ("a", shortLived)));
    }

    [Fact]
    public async Task The_proforma_carries_the_issuer_the_customer_and_ITBIS_at_the_rule_in_force()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        var proforma = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrderProforma(h.CompanyId, w.S.Seller, w.Order), new GetSalesOrderProformaHandler())).RootElement;

        // 1,000 blocks × 50.00 = 50,000.00; ITBIS 18 % = 9,000.00.
        Assert.Equal("BLOQUE-6:1000.000000:50000.0000:9000.00|50000.0000:9000.00:59000.0000", string.Join('|',
            proforma.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("itemCode").GetString()}:{l.GetProperty("quantity").GetString()}:{l.GetProperty("net").GetString()}:{l.GetProperty("itbis").GetString()}")
                .Append($"{proforma.GetProperty("netTotal").GetString()}:{proforma.GetProperty("itbisTotal").GetString()}:{proforma.GetProperty("total").GetString()}")));
        Assert.Equal(await h.ScalarAsync<string>("SELECT rnc FROM md.company WHERE company_id = @c", ("c", h.CompanyId)), proforma.GetProperty("issuerRnc").GetString());
        Assert.Equal("131925332", proforma.GetProperty("customerRnc").GetString());
    }

    private static DateOnly BusinessCalendarToday(FakeClock clock) => Platform.Time.BusinessCalendar.DefaultBusinessDate(clock.UtcNow);
}
