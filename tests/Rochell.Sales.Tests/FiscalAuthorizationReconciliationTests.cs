using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Commands;
using Rochell.Reconciliation;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Invoices;
using Rochell.Tax.Authorizations;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1-04: AUTH-CONSUMPTION, EXEMPT-WITHOUT-AUTH, AUTH-EXPIRY and the expiry command (E-FIS1-04-1…6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalAuthorizationReconciliationTests(PostgresFixture postgres)
{
    private static readonly string[] Codes = ["AUTH-CONSUMPTION", "AUTH-EXPIRY", "EXEMPT-WITHOUT-AUTH"];

    private static async Task<JsonElement> ReconcileAsync(TestHarness h, string key)
        => JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, await h.SessionWithRolesAsync("CONTROLLER"), key, Codes), new RunReconciliationHandler())).ResultPayload).RootElement;

    private static string Statuses(JsonElement result)
        => string.Join(',', result.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal));

    private static Task<string?> FindingsAsync(TestHarness h)
        => h.ScalarAsync<string>("SELECT string_agg(DISTINCT r.recon_code || ':' || x.classification, ',') FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id)");

    /// <summary>The month: an e-CF 44 of 600 blocks (30,000.00) recorded as E44, a credit note of 1,000.00 recorded as E34, and a voided e-CF 44 of 100 blocks.</summary>
    private static async Task<ExemptInvoiceTests.World> MonthAsync(TestHarness h)
    {
        var w = await ExemptInvoiceTests.WorldAsync(h);
        var invoice = (await ExemptInvoiceTests.Create(h, w, await ExemptInvoiceTests.DeliveredAsync(h, w, 600m, "d1"), "i1")).ResultRef;
        await ExemptInvoiceTests.Issue(h, w, invoice, "issue-1");
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.Billing, "fisc", invoice, 2, "E440000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash,
                "131925332", 30000.00m, 0m, 30000.00m),
            new RecordExternalFiscalDocumentHandler());
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", invoice));
        var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, w.Billing, "nc", invoice, "DESCUENTO", "Descuento por volumen", [new(invoiceLine, 1000.00m)]), new CreateCreditNoteHandler())).ResultRef;
        await h.RunAsync(new IssueCreditNote(h.CompanyId, await h.SessionWithRolesAsync("FACTURACION"), "nc-i", note, 1), new IssueCreditNoteHandler());
        await h.RunAsync(
            new RecordExternalCreditNoteDocument(h.CompanyId, w.Billing, "nc-f", note, 2, "E340000000001", h.Clock.UtcNow.AddMinutes(-1), "Z9", "nc.xml", DeliveryTests.Hash,
                "131925332", 1000.00m, 0m, 1000.00m),
            new RecordExternalCreditNoteDocumentHandler());
        var voided = (await ExemptInvoiceTests.Create(h, w, await ExemptInvoiceTests.DeliveredAsync(h, w, 100m, "d2"), "i2")).ResultRef;
        await ExemptInvoiceTests.Issue(h, w, voided, "issue-2");
        await h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, await h.SessionWithRolesAsync("CONTROLLER"), "void", voided, 2, "Error de cantidad"), new VoidUnfiscalizedInvoiceHandler());
        return w;
    }

    [Trait("AcceptanceFis1", "FIS-09")]
    [Fact]
    public async Task FIS09_a_month_with_exempt_sales_reconciles_and_AR_REC_closes()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await MonthAsync(h);
        var month = ReceiptTests.Today(h);
        var run = await ReconcileAsync(h, "recon");

        clock.Advance(TimeSpan.FromDays(40));
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));
        await h.RunAsync(new CloseComponent(h.CompanyId, await h.SessionWithRolesAsync("CONTROLLER"), "close", period, "AR-REC"), new CloseComponentHandler());

        // Without the alert threshold AUTH-EXPIRY cannot run once authorizations exist (E-FIS1-04-4); it is a warning and blocks nothing.
        Assert.Equal("AUTH-CONSUMPTION:MATCHED,AUTH-EXPIRY:FAILED,EXEMPT-WITHOUT-AUTH:MATCHED", Statuses(run));
        Assert.Equal("ACTIVE:600.000000:29000.0000", await h.ScalarAsync<string>(
            "SELECT a.status || ':' || l.qty_consumed || ':' || l.net_consumed FROM tax.fiscal_authorization a JOIN tax.fiscal_authorization_line l USING (authorization_id) WHERE a.authorization_id = @a",
            ("a", w.Authorization)));
        Assert.Equal("CLOSED", await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'AR-REC'"));
        Assert.Equal("AUTH-CONSUMPTION:MATCHED,EXEMPT-WITHOUT-AUTH:MATCHED", await h.ScalarAsync<string>(
            """
            SELECT string_agg((r ->> 'code') || ':' || (r ->> 'status'), ',' ORDER BY r ->> 'code')
            FROM fin.close_snapshot s CROSS JOIN jsonb_array_elements(s.content -> 'reconciliations') r
            WHERE s.component = 'AR-REC' AND r ->> 'code' IN ('AUTH-CONSUMPTION', 'EXEMPT-WITHOUT-AUTH')
            """));
    }

    [Fact]
    public async Task Tampered_consumption_and_an_invoice_without_ITBIS_outside_eCF_44_are_found_and_block_the_AR_REC_close()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await MonthAsync(h);
        var month = ReceiptTests.Today(h);
        var taxed = (await ExemptInvoiceTests.Create(h, w, await ExemptInvoiceTests.DeliveredAsync(h, w, 10m, "d3"), "i3", exempt: false)).ResultRef;
        await ExemptInvoiceTests.Issue(h, w, taxed, "issue-3");
        await h.AdminRequireAsync(
            $"""
            SET LOCAL session_replication_role = replica;
            UPDATE tax.fiscal_authorization_line SET net_consumed = net_consumed + 1 WHERE authorization_id = '{w.Authorization}';
            UPDATE sal.invoice SET tax_total = 0, total = net_total WHERE invoice_id = '{taxed}';
            """);

        var run = await ReconcileAsync(h, "recon");
        clock.Advance(TimeSpan.FromDays(40));
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));
        var controller = await h.SessionWithRolesAsync("CONTROLLER"); // the month's sessions expired
        var blocked = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "AR-REC"), new CloseComponentHandler()));

        Assert.Equal("AUTH-CONSUMPTION:EXCEPTIONS,AUTH-EXPIRY:FAILED,EXEMPT-WITHOUT-AUTH:EXCEPTIONS", Statuses(run));
        Assert.Equal("AUTH-CONSUMPTION:AUTH_LINE_CONSUMPTION_DIFFERENCE,EXEMPT-WITHOUT-AUTH:EXEMPT_WITHOUT_AUTHORIZATION", await FindingsAsync(h));
        Assert.Equal(ReconciliationErrors.ReconciliationErrorsFound, blocked.Code);
        Assert.Contains("AUTH-CONSUMPTION, EXEMPT-WITHOUT-AUTH", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUTH_EXPIRY_warns_ahead_and_the_expiry_command_moves_lapsed_authorizations_to_EXPIRED()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var today = ReceiptTests.Today(h);
        var w = await ExemptInvoiceTests.WorldAsync(h, validUntil: today.AddDays(20));

        // The delivery setup's REVENUE_ACCOUNTING version, given the alert threshold (fixture: parameters of an ACTIVE version are frozen).
        await h.AdminRequireAsync(
            $"""
            SET LOCAL session_replication_role = replica;
            INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value)
            SELECT company_id, policy_version_id, 'authorization_expiry_alert_days', to_jsonb('30'::text)
            FROM acc.accounting_policy_version WHERE company_id = '{h.CompanyId}' AND policy_code = 'REVENUE_ACCOUNTING' AND status = 'ACTIVE';
            """);

        var warned = await ReconcileAsync(h, "recon");
        var early = JsonDocument.Parse((await h.RunAsync(new ExpireFiscalAuthorizations(h.CompanyId, w.Specialist, "exp-0"), new ExpireFiscalAuthorizationsHandler())).ResultPayload).RootElement;
        clock.Advance(TimeSpan.FromDays(22));
        var specialist = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var notAllowed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ExpireFiscalAuthorizations(h.CompanyId, billing, "exp-b"), new ExpireFiscalAuthorizationsHandler()));
        var expired = JsonDocument.Parse((await h.RunAsync(new ExpireFiscalAuthorizations(h.CompanyId, specialist, "exp-1"), new ExpireFiscalAuthorizationsHandler())).ResultPayload).RootElement;
        var again = JsonDocument.Parse((await h.RunAsync(new ExpireFiscalAuthorizations(h.CompanyId, specialist, "exp-2"), new ExpireFiscalAuthorizationsHandler())).ResultPayload).RootElement;
        var after = await ReconcileAsync(h, "recon-2");

        Assert.Equal("AUTH-CONSUMPTION:MATCHED,AUTH-EXPIRY:EXCEPTIONS,EXEMPT-WITHOUT-AUTH:MATCHED", Statuses(warned));
        Assert.Equal("AUTH-EXPIRY:AUTHORIZATION_EXPIRING", await FindingsAsync(h));
        Assert.Equal((0, 1, 0), (early.GetProperty("expired").GetArrayLength(), expired.GetProperty("expired").GetArrayLength(), again.GetProperty("expired").GetArrayLength()));
        Assert.Equal(AuthorizationErrors.NotAuthorized, notAllowed.Code);
        Assert.Equal("EXPIRED:4", await h.ScalarAsync<string>("SELECT status || ':' || version FROM tax.fiscal_authorization WHERE authorization_id = @a", ("a", w.Authorization)));
        Assert.Equal("ACTIVE>EXPIRED:Tax.ExpireFiscalAuthorizations", await h.ScalarAsync<string>(
            "SELECT from_state || '>' || to_state || ':' || command FROM core.state_history WHERE aggregate_id = @a ORDER BY state_history_id DESC LIMIT 1", ("a", w.Authorization)));
        Assert.Equal("AUTH-CONSUMPTION:MATCHED,AUTH-EXPIRY:MATCHED,EXEMPT-WITHOUT-AUTH:MATCHED", Statuses(after)); // an EXPIRED authorization is no longer in use
    }
}
