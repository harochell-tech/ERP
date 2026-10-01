using System.Text.Json;
using Rochell.MasterData;
using Rochell.Platform.Commands;
using Rochell.Sales.Customers;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>IMP-01: the customer file, its preview and import, terms and activation of several customers, e-mail lists (E-IMP-1…11).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CustomerImportTests(PostgresFixture postgres)
{
    private static readonly string?[] Header =
        ["Razón Social", "Teléfono 1", "Correo Electrónico", "Moneda", "ID Fiscal", "Tipo ID Fiscal", "Término de Pago", "Forma de Pago", "Límite de Crédito"];

    /// <summary>Synthetic rows in the shape of the ADM Cloud export (E-IMP-11).</summary>
    private static string File() => TestSpreadsheet.Base64(
        Header,
        ["Constructora Uno SRL", "809-555-0101", "cxp@uno.test; compras@uno.test", "DOP", "1-31-92533-2", "ID Empresa", "30 días", "Cheque", "150,000.00"],
        ["Persona Dos", null, null, "DOP", "00112345678", "ID Personal", null, "Efectivo", null],
        ["Proveedor Tres (nombre del archivo)", "809-555-0303", "ventas@tres.test", "DOP", "130000001", "ID Empresa", "Contado", "Transferencia", null],
        ["CLIENTE CONSUMIDOR FINAL", null, null, "DOP", null, null, null, "Efectivo", null],
        ["Ya Cliente SRL", null, null, "DOP", "101000029", null, "15 días", "Cheque", null],
        ["Límite Malo SRL", null, null, "DOP", "101000045", null, null, "Cheque", "-5"]);

    private sealed record Actors(Guid Seller, Guid Credit, Guid Controller);

    private static async Task<Actors> ActorsAsync(TestHarness h)
        => new(await h.SessionWithRolesAsync("VENDEDOR"), await h.SessionWithRolesAsync("CREDITO"), await h.SessionWithRolesAsync("CONTROLLER"));

    private static string Outcomes(JsonElement report)
        => string.Join('|', report.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("row").GetInt32()}:{i.GetProperty("outcome").GetString()}:{i.GetProperty("reasonCode").GetString()}"));

    private static async Task<(JsonElement Report, Guid Supplier, Guid Existing)> ImportAsync(TestHarness h, Actors a)
    {
        var supplier = await h.CreateActiveSupplierAsync("130000001", "Proveedor Tres SRL");
        var existing = (await h.RunAsync(new CreateCustomer(h.CompanyId, a.Seller, "existing", "101000029", "Ya Cliente SRL"), new CreateCustomerHandler())).ResultRef;
        var result = await h.RunAsync(new ImportCustomers(h.CompanyId, a.Seller, "import", "Clientes.xlsx", File()), new ImportCustomersHandler());
        return (JsonDocument.Parse(result.ResultPayload).RootElement, supplier, existing);
    }

    [Fact]
    public async Task The_preview_says_what_each_row_would_do_and_the_import_leaves_draft_customers_with_draft_terms()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var supplier = await h.CreateActiveSupplierAsync("130000001", "Proveedor Tres SRL");
        await h.RunAsync(new CreateCustomer(h.CompanyId, a.Seller, "existing", "101000029", "Ya Cliente SRL"), new CreateCustomerHandler());

        var preview = JsonDocument.Parse(await h.QueryAsync(new PreviewCustomerImport(h.CompanyId, a.Seller, "Clientes.xlsx", File()), new PreviewCustomerImportHandler())).RootElement;
        var before = await h.ScalarAsync<long>("SELECT count(*) FROM md.party");
        var denied = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ImportCustomers(h.CompanyId, a.Credit, "denied", "Clientes.xlsx", File()), new ImportCustomersHandler()));
        var report = JsonDocument.Parse((await h.RunAsync(new ImportCustomers(h.CompanyId, a.Seller, "import", "Clientes.xlsx", File()), new ImportCustomersHandler())).ResultPayload).RootElement;

        const string Expected = "2:CREATE:|3:CREATE:|4:LINK:|5:REJECTED:ID_MISSING|6:EXISTS:ALREADY_CUSTOMER|7:REJECTED:CREDIT_LIMIT_INVALID";
        Assert.Equal(Expected, Outcomes(preview));
        Assert.Equal(Expected, Outcomes(report));
        Assert.Equal((2L, AuthorizationErrors.NotAuthorized), (before, denied.Code));
        Assert.Equal(
            (6, 2, 1, 1, 2, "Moneda,Tipo ID Fiscal,Forma de Pago"),
            (report.GetProperty("rows").GetInt32(), report.GetProperty("toCreate").GetInt32(), report.GetProperty("toLink").GetInt32(), report.GetProperty("existing").GetInt32(),
             report.GetProperty("rejected").GetInt32(), string.Join(',', report.GetProperty("ignoredColumns").EnumerateArray().Select(c => c.GetString()))));

        // New customers: DRAFT, with their contact data and DRAFT terms (E-IMP-7, E-IMP-8, E-IMP-01-7).
        Assert.Equal(
            "131925332|Constructora Uno SRL|DRAFT|DRAFT|false|809-555-0101|cxp@uno.test|cxp@uno.test,compras@uno.test|30|150000.00|DRAFT"
            + "\n00112345678|Persona Dos|DRAFT|DRAFT|false|||none|0|0.00|DRAFT",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(concat_ws('|', p.rnc, p.legal_name, p.status, p.customer_status, p.is_supplier::text, coalesce(p.phone, ''), coalesce(p.email, ''),
                         coalesce((SELECT string_agg(e.email, ',' ORDER BY e.position) FROM md.party_email e WHERE e.party_id = p.party_id), 'none'),
                         t.payment_terms_days, t.credit_limit::numeric(19,2), t.status), E'\n' ORDER BY p.legal_name)
                FROM md.party p JOIN sal.customer_terms_version t ON t.party_id = p.party_id
                WHERE p.rnc IN ('131925332', '00112345678')
                """));

        // E-IMP-5: the supplier keeps its name and becomes a customer too; its empty contact data takes the file's.
        Assert.Equal(
            "Proveedor Tres SRL|ACTIVE|DRAFT|true|809-555-0303|ventas@tres.test|0|0.00",
            await h.ScalarAsync<string>(
                """
                SELECT concat_ws('|', p.legal_name, p.status, p.customer_status, p.is_supplier::text, p.phone, p.email, t.payment_terms_days, t.credit_limit::numeric(19,2))
                FROM md.party p JOIN sal.customer_terms_version t ON t.party_id = p.party_id WHERE p.party_id = @p
                """,
                ("p", supplier)));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'Customer' AND to_state = 'DRAFT' AND command = 'Sales.ImportCustomers'"));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'CustomerTerms' AND to_state = 'DRAFT' AND command = 'Sales.ImportCustomers'"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.domain_event WHERE event_type = 'CustomersImported' AND payload->>'sha256' = @h", ("h", report.GetProperty("sha256").GetString()!)));
    }

    [Fact]
    public async Task The_controller_approves_several_terms_and_credit_activates_several_customers()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var (report, _, existing) = await ImportAsync(h, a);
        var customers = report.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("outcome").GetString() is "CREATE" or "LINK").Select(i => i.GetProperty("partyId").GetGuid()).ToList();
        var drafts = JsonDocument.Parse(await h.QueryAsync(new ListCustomerTerms(h.CompanyId, a.Controller, "DRAFT"), new ListCustomerTermsHandler())).RootElement
            .GetProperty("items").EnumerateArray().Select(t => t.GetProperty("termsVersionId").GetGuid()).ToList();
        var unknown = Guid.CreateVersion7();

        var early = JsonDocument.Parse((await h.RunAsync(new ActivateCustomers(h.CompanyId, a.Credit, "early", customers), new ActivateCustomersHandler())).ResultPayload).RootElement;
        var bySeller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveCustomerTermsBatch(h.CompanyId, a.Seller, "seller", drafts), new ApproveCustomerTermsBatchHandler()));
        var approved = JsonDocument.Parse((await h.RunAsync(new ApproveCustomerTermsBatch(h.CompanyId, a.Controller, "approve", [.. drafts, unknown]), new ApproveCustomerTermsBatchHandler())).ResultPayload).RootElement;
        var again = JsonDocument.Parse((await h.RunAsync(new ApproveCustomerTermsBatch(h.CompanyId, a.Controller, "approve-2", drafts), new ApproveCustomerTermsBatchHandler())).ResultPayload).RootElement;
        var activated = JsonDocument.Parse((await h.RunAsync(new ActivateCustomers(h.CompanyId, a.Credit, "activate", [.. customers, existing]), new ActivateCustomersHandler())).ResultPayload).RootElement;
        var tooMany = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ActivateCustomers(h.CompanyId, a.Credit, "many", [.. Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7())]), new ActivateCustomersHandler()));

        Assert.Equal(3, drafts.Count);
        Assert.Equal((0, 3, SalesErrors.TermsRequired), (early.GetProperty("activated").GetInt32(), early.GetProperty("skipped").GetInt32(), early.GetProperty("items")[0].GetProperty("code").GetString()));
        Assert.Equal((AuthorizationErrors.NotAuthorized, MasterDataErrors.BatchInvalid), (bySeller.Code, tooMany.Code));
        Assert.Equal((4, 3, 1), (approved.GetProperty("requested").GetInt32(), approved.GetProperty("approved").GetInt32(), approved.GetProperty("skipped").GetInt32()));
        Assert.Equal(
            SalesErrors.NotFound,
            approved.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("outcome").GetString() == "SKIPPED").GetProperty("code").GetString());
        Assert.Equal((0, SalesErrors.InvalidState), (again.GetProperty("approved").GetInt32(), again.GetProperty("items")[0].GetProperty("code").GetString()));
        Assert.Equal((3, 1), (activated.GetProperty("activated").GetInt32(), activated.GetProperty("skipped").GetInt32()));
        Assert.Equal(
            $"{existing}:{SalesErrors.TermsRequired}",
            string.Join('|', activated.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("outcome").GetString() == "SKIPPED").Select(i => $"{i.GetProperty("partyId").GetGuid()}:{i.GetProperty("code").GetString()}")));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM md.party WHERE customer_status = 'ACTIVE' AND status = 'ACTIVE'"));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM sal.customer_terms_version WHERE status = 'ACTIVE' AND approved_by <> prepared_by"));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'Customer' AND to_state = 'ACTIVE' AND command = 'Sales.ActivateCustomers'"));
    }

    [Fact]
    public async Task A_customer_keeps_several_e_mails_and_the_first_is_the_principal_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var party = (await h.RunAsync(
            new CreateCustomer(h.CompanyId, a.Seller, "c", "131925332", "Constructora Uno", Emails: ["cxp@uno.test", "compras@uno.test", "CXP@uno.test"]), new CreateCustomerHandler())).ResultRef;

        async Task<string> EmailsAsync()
        {
            var detail = JsonDocument.Parse(await h.QueryAsync(new GetCustomer(h.CompanyId, a.Seller, party), new GetCustomerHandler())).RootElement;
            return $"{detail.GetProperty("email").GetString()}|{string.Join(',', detail.GetProperty("emails").EnumerateArray().Select(e => e.GetString()))}";
        }

        var created = await EmailsAsync();
        // The single field (the form before E-IMP-6) replaces only the principal e-mail.
        await h.RunAsync(new UpdateCustomer(h.CompanyId, a.Seller, "u1", party, 1, "131925332", "Constructora Uno", null, "pagos@uno.test", null), new UpdateCustomerHandler());
        var single = await EmailsAsync();
        await h.RunAsync(new UpdateCustomer(h.CompanyId, a.Seller, "u2", party, 2, "131925332", "Constructora Uno", null, null, null, ["a@uno.test", "b@uno.test"]), new UpdateCustomerHandler());
        var list = await EmailsAsync();
        var invalid = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateCustomer(h.CompanyId, a.Seller, "u3", party, 3, "131925332", "Constructora Uno", null, null, null, ["sin-arroba"]), new UpdateCustomerHandler()));
        await h.RunAsync(new UpdateCustomer(h.CompanyId, a.Seller, "u4", party, 3, "131925332", "Constructora Uno", null, null, null, []), new UpdateCustomerHandler());

        Assert.Equal("cxp@uno.test|cxp@uno.test,compras@uno.test", created);
        Assert.Equal("pagos@uno.test|pagos@uno.test,compras@uno.test", single);
        Assert.Equal("a@uno.test|a@uno.test,b@uno.test", list);
        Assert.Equal(SalesErrors.FieldInvalid, invalid.Code);
        Assert.Equal("|", await EmailsAsync());
    }
}
