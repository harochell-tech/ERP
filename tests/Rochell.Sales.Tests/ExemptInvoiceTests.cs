using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.Tax;
using Rochell.Tax.Authorizations;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1-03: exempt e-CF 44 invoices under a CONFOTUR authorization — consumption, void, credit note, external e-CF (E-FIS1-03-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExemptInvoiceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private sealed record World(DeliveryTests.Setup S, Guid Billing, Guid Specialist, Guid Order, Guid OrderLine, Guid Authorization);

    /// <summary>An order of 2,000 blocks at 50.00 and an ACTIVE authorization for 1,000 blocks / 50,000.00.</summary>
    private static async Task<World> WorldAsync(TestHarness h, DateOnly? validUntil = null)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 2000m);
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var specialist = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");
        var authorization = (await h.RunAsync(
            new RegisterFiscalAuthorization(h.CompanyId, billing, "auth", s.Customer, "CERT-2026-0001", new DateOnly(2026, 9, 1), validUntil ?? new DateOnly(2027, 3, 1), "Hotel Playa Bávaro",
                "CONFOTUR-0456-2025", null, order, [new AuthorizationLineInput(s.Block, "un", 1000m, 50000.00m)]),
            new RegisterFiscalAuthorizationHandler())).ResultRef;
        await h.RunAsync(new AttachAuthorizationDocument(h.CompanyId, billing, "doc", authorization, "CERTIFICADO_DGII", "certificado.pdf", DeliveryTests.Hash), new AttachAuthorizationDocumentHandler());
        await h.RunAsync(new SubmitForVerification(h.CompanyId, billing, "sub", authorization, 1), new SubmitForVerificationHandler());
        await h.RunAsync(new VerifyAuthorization(h.CompanyId, specialist, "ver", authorization, 2), new VerifyAuthorizationHandler());
        return new World(s, billing, specialist, order, orderLine, authorization);
    }

    private static async Task<Guid> DeliveredAsync(TestHarness h, World w, decimal quantity, string key)
        => (await DeliveryTests.DispatchAsync(h, w.S, w.Order, w.OrderLine, quantity, own: false, key)).Line;

    private static Task<CommandResult> Create(TestHarness h, World w, Guid deliveryLine, string key, bool exempt = true)
        => h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.Billing, key, w.S.Customer, [deliveryLine], exempt ? w.Authorization : null), new CreateInvoiceFromDeliveriesHandler());

    private static Task<CommandResult> Issue(TestHarness h, World w, Guid invoice, string key, Guid? session = null)
        => h.RunAsync(new IssueInvoice(h.CompanyId, session ?? w.Billing, key, invoice, 1), new IssueInvoiceHandler());

    private static Task<string?> Consumed(TestHarness h, World w)
        => h.ScalarAsync<string>("SELECT a.status || ':' || l.qty_consumed || ':' || l.net_consumed FROM tax.fiscal_authorization a JOIN tax.fiscal_authorization_line l USING (authorization_id) WHERE a.authorization_id = @a", ("a", w.Authorization));

    [Trait("AcceptanceFis1", "FIS-02")]
    [Fact]
    public async Task FIS02_an_exempt_invoice_is_an_eCF_44_without_ITBIS_that_consumes_the_authorization()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var line = await DeliveredAsync(h, w, 600m, "d1");

        var invoice = (await Create(h, w, line, "i")).ResultRef;
        var issued = JsonDocument.Parse((await Issue(h, w, invoice, "issue")).ResultPayload).RootElement;

        Assert.Equal("0.00|30000.00", $"{issued.GetProperty("taxTotal").GetString()}|{issued.GetProperty("total").GetString()}");
        Assert.Equal("44", await h.ScalarAsync<string>("SELECT ecf_type FROM sal.invoice WHERE invoice_id = @i", ("i", invoice)));
        Assert.Equal("ACTIVE:600.000000:30000.0000", await Consumed(h, w));
        Assert.Equal("AR_CONTROL=30000.00|CONTRACT_ASSET=0.00|ITBIS_PAYABLE=0.00|REVENUE_PRODUCT=-30000.00",
            await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "CONTRACT_ASSET", "ITBIS_PAYABLE", "REVENUE_PRODUCT"));
        Assert.Equal("CONFOTUR:CERT-2026-0001:0", await h.ScalarAsync<string>(
            """
            SELECT d.inputs -> 'exemption' ->> 'regime' || ':' || (d.inputs -> 'exemption' ->> 'certificateNo') || ':' || (SELECT count(*) FROM tax.tax_determination_line l WHERE l.determination_id = d.determination_id)
            FROM sal.invoice i JOIN tax.tax_determination d ON d.determination_id = i.tax_determination_id WHERE i.invoice_id = @i
            """,
            ("i", invoice)));
        var package = JsonDocument.Parse(await h.QueryAsync(new GetInvoiceFiscalPackage(h.CompanyId, w.S.Seller, invoice), new GetInvoiceFiscalPackageHandler())).RootElement;
        Assert.Equal("44|0.00|CONFOTUR|CERT-2026-0001|4", $"{package.GetProperty("ecfType").GetString()}|{package.GetProperty("taxTotal").GetString()}|" +
            $"{package.GetProperty("exemption").GetProperty("regime").GetString()}|{package.GetProperty("exemption").GetProperty("certificateNo").GetString()}|{package.GetProperty("exemption").GetProperty("billingIndicator").GetString()}");
    }

    [Trait("AcceptanceFis1", "FIS-03")]
    [Trait("AcceptanceFis1", "FIS-04")]
    [Trait("AcceptanceFis1", "FIS-05")]
    [Fact]
    public async Task FIS03_04_05_beyond_the_scope_out_of_it_or_while_suspended_the_invoice_carries_ITBIS()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await WorldAsync(h, BusinessCalendarToday(clock).AddDays(10));
        await Issue(h, w, (await Create(h, w, await DeliveredAsync(h, w, 600m, "d1"), "i1")).ResultRef, "issue-1");
        var big = await DeliveredAsync(h, w, 500m, "d2");

        var exceeded = await Assert.ThrowsAsync<DomainException>(() => Create(h, w, big, "i2"));
        var taxed = JsonDocument.Parse((await Issue(h, w, (await Create(h, w, big, "i2b", exempt: false)).ResultRef, "issue-2")).ResultPayload).RootElement;
        var paver = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{paver}', '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'ACTIVE', 1)");
        var other = (await h.RunAsync(
            new RegisterFiscalAuthorization(h.CompanyId, w.Billing, "auth-2", w.S.Customer, "CERT-2026-0002", new DateOnly(2026, 9, 1), null, "Hotel Playa Bávaro", "CONFOTUR-0456-2025", null, null,
                [new AuthorizationLineInput(paver, "un", 100m, 4000.00m)]),
            new RegisterFiscalAuthorizationHandler())).ResultRef;
        await h.RunAsync(new AttachAuthorizationDocument(h.CompanyId, w.Billing, "doc-2", other, "CERTIFICADO_DGII", "c.pdf", DeliveryTests.Hash), new AttachAuthorizationDocumentHandler());
        await h.RunAsync(new SubmitForVerification(h.CompanyId, w.Billing, "sub-2", other, 1), new SubmitForVerificationHandler());
        await h.RunAsync(new VerifyAuthorization(h.CompanyId, w.Specialist, "ver-2", other, 2), new VerifyAuthorizationHandler());
        var small = await DeliveredAsync(h, w, 100m, "d3");
        var notCovered = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromDeliveries(h.CompanyId, w.Billing, "i3", w.S.Customer, [small], other), new CreateInvoiceFromDeliveriesHandler()));
        var draft = (await Create(h, w, small, "i4")).ResultRef;
        await h.RunAsync(new SuspendAuthorization(h.CompanyId, w.Specialist, "sus", w.Authorization, 3, "Revisión"), new SuspendAuthorizationHandler());
        var suspended = await Assert.ThrowsAsync<DomainException>(() => Issue(h, w, draft, "issue-4"));
        await h.RunAsync(new ReactivateAuthorization(h.CompanyId, w.Specialist, "rea", w.Authorization, 4, "Confirmada"), new ReactivateAuthorizationHandler());
        clock.Advance(TimeSpan.FromDays(12));
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var expired = await Assert.ThrowsAsync<DomainException>(() => Issue(h, w, draft, "issue-5", billing));
        var retype = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new IssueInvoice(h.CompanyId, billing, "issue-6", draft, 1, "31"), new IssueInvoiceHandler()));

        Assert.Equal(
            (TaxErrors.AuthorizationExceeded, TaxErrors.AuthorizationNotCovered, TaxErrors.AuthorizationInvalidState, TaxErrors.AuthorizationExpired, InvoiceErrors.EcfTypeInvalid),
            (exceeded.Code, notCovered.Code, suspended.Code, expired.Code, retype.Code));
        Assert.Equal("4500.00|29500.00", $"{taxed.GetProperty("taxTotal").GetString()}|{taxed.GetProperty("total").GetString()}"); // 500 × 50.00 = 25,000.00 + 18 %
        Assert.Equal("ACTIVE:600.000000:30000.0000", await Consumed(h, w));
    }

    [Trait("AcceptanceFis1", "FIS-06")]
    [Trait("AcceptanceFis1", "FIS-08")]
    [Fact]
    public async Task FIS06_08_the_eCF_44_is_recorded_as_E44_and_a_void_or_a_credit_note_returns_the_consumption()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var first = (await Create(h, w, await DeliveredAsync(h, w, 980m, "d1"), "i1")).ResultRef;
        await Issue(h, w, first, "issue-1");
        var last = (await Create(h, w, await DeliveredAsync(h, w, 20m, "d2"), "i2")).ResultRef;
        await Issue(h, w, last, "issue-2");
        var exhausted = await Consumed(h, w);
        await h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, await h.SessionWithRolesAsync("CONTROLLER"), "void", last, 2, "Error de cantidad"), new VoidUnfiscalizedInvoiceHandler());
        var afterVoid = await Consumed(h, w);
        RecordExternalFiscalDocument Record(string encf) => new(h.CompanyId, w.Billing, "fisc-" + encf, first, 2, encf, h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash,
            "131925332", 49000.00m, 0m, 49000.00m);

        var wrongType = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Record("E310000000001"), new RecordExternalFiscalDocumentHandler()));
        await h.RunAsync(Record("E440000000001"), new RecordExternalFiscalDocumentHandler());
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", first));
        var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, w.Billing, "nc", first, "DESCUENTO", "Descuento por volumen", [new(invoiceLine, 1000.00m)]), new CreateCreditNoteHandler())).ResultRef;
        await h.RunAsync(new IssueCreditNote(h.CompanyId, await h.SessionWithRolesAsync("FACTURACION"), "nc-i", note, 1), new IssueCreditNoteHandler());

        Assert.Equal(InvoiceErrors.EncfInvalid, wrongType.Code);
        Assert.Equal("EXHAUSTED:1000.000000:50000.0000", exhausted);
        Assert.Equal("ACTIVE:980.000000:49000.0000", afterVoid); // the void returned the 20 units and 1,000.00
        Assert.Equal("0.00", await h.ScalarAsync<string>("SELECT tax_total::numeric(19,2)::text FROM sal.credit_note WHERE credit_note_id = @n", ("n", note)));
        Assert.Equal("ACTIVE:980.000000:48000.0000", await Consumed(h, w)); // the price credit returns 1,000.00 of net, no quantity
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM tax.fiscal_authorization_consumption WHERE reverses_consumption_id IS NOT NULL"));
    }

    [Trait("AcceptanceFis1", "FIS-07")]
    [Fact]
    public async Task FIS07_two_exempt_invoices_issued_at_once_for_the_whole_balance_consume_it_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var first = (await Create(h, w, await DeliveredAsync(h, w, 600m, "d1"), "i1")).ResultRef;
        var second = (await Create(h, w, await DeliveredAsync(h, w, 600m, "d2"), "i2")).ResultRef;
        var billing2 = await h.SessionWithRolesAsync("FACTURACION");

        var outcomes = await Task.WhenAll(new[] { (first, w.Billing, "a"), (second, billing2, "b") }.Select(x => Task.Run(async () =>
        {
            try
            {
                await Issue(h, w, x.Item1, "issue-" + x.Item3, x.Item2);
                return (string?)null;
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
        })));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Equal(TaxErrors.AuthorizationExceeded, Assert.Single(outcomes, o => o is not null));
        Assert.Equal("ACTIVE:600.000000:30000.0000", await Consumed(h, w));
    }

    private static DateOnly BusinessCalendarToday(FakeClock clock) => Platform.Time.BusinessCalendar.DefaultBusinessDate(clock.UtcNow);
}
