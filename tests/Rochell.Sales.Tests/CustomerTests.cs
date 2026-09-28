using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Customers;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-02: customers and their terms (E-VS3-01-1/8/9/10, E-VS3-17 (a), E-VS3-02-3…6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CustomerTests(PostgresFixture postgres)
{
    private sealed record Actors(Guid Seller, Guid Credit, Guid Controller);

    private static async Task<Actors> ActorsAsync(TestHarness h)
        => new(await h.SessionWithRolesAsync("VENDEDOR"), await h.SessionWithRolesAsync("CREDITO"), await h.SessionWithRolesAsync("CONTROLLER"));

    private static async Task<Guid> Terms(TestHarness h, Actors a, Guid party, string key, int days = 30, decimal limit = 100000.00m, bool approve = true)
    {
        var id = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, a.Credit, key, party, days, limit, false), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        if (approve)
        {
            await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, a.Controller, key + "-a", id), new ApproveCustomerTermsHandler());
        }

        return id;
    }

    [Fact]
    public async Task A_new_customer_is_created_by_the_seller_given_terms_by_credit_and_the_controller_and_activated_by_credit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var party = (await h.RunAsync(new CreateCustomer(h.CompanyId, a.Seller, "c", "131-92533-2", "Constructora Uno", "809-555-0101", "compras@uno.do", "Higüey"), new CreateCustomerHandler())).ResultRef;

        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateCustomer(h.CompanyId, a.Credit, "early", party, 1), new ActivateCustomerHandler()));
        var draftTerms = await Terms(h, a, party, "t1", approve: false);
        var sellerActivates = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateCustomer(h.CompanyId, a.Seller, "seller", party, 1), new ActivateCustomerHandler()));
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, a.Controller, "t1-a", draftTerms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, a.Credit, "activate", party, 1), new ActivateCustomerHandler());

        Assert.Equal((SalesErrors.TermsRequired, AuthorizationErrors.NotAuthorized), (early.Code, sellerActivates.Code));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetCustomer(h.CompanyId, a.Seller, party), new GetCustomerHandler())).RootElement;
        Assert.Equal(
            ("131925332", "ACTIVE", "ACTIVE", false, "Higüey", 2L),
            (detail.GetProperty("rnc").GetString(), detail.GetProperty("partyStatus").GetString(), detail.GetProperty("customerStatus").GetString(),
             detail.GetProperty("isSupplier").GetBoolean(), detail.GetProperty("address").GetString(), detail.GetProperty("version").GetInt64()));
        Assert.Equal("30:100000.00:ACTIVE", string.Join(':', detail.GetProperty("terms")[0].GetProperty("paymentTermsDays").GetInt32(),
            detail.GetProperty("terms")[0].GetProperty("creditLimit").GetString(), detail.GetProperty("terms")[0].GetProperty("status").GetString()));
        Assert.Equal("DRAFT,ACTIVE", string.Join(',', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));
    }

    [Fact]
    public async Task A_supplier_with_the_same_RNC_becomes_the_customer_and_keeps_its_identity()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var supplier = await h.CreateActiveSupplierAsync("131925332", "Constructora Uno");

        var result = JsonDocument.Parse((await h.RunAsync(new CreateCustomer(h.CompanyId, a.Seller, "c", "131925332", "Otro nombre", Phone: "809-555-0102"), new CreateCustomerHandler())).ResultPayload).RootElement;
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateCustomer(h.CompanyId, a.Seller, "c2", "131925332", "Otro"), new CreateCustomerHandler()));
        var rename = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateCustomer(h.CompanyId, a.Seller, "rename", supplier, 2, "131925332", "Nuevo nombre", null, null, null), new UpdateCustomerHandler()));
        await h.RunAsync(new UpdateCustomer(h.CompanyId, a.Seller, "contact", supplier, 2, "131925332", "Constructora Uno", "809-555-0199", "pagos@uno.do", null), new UpdateCustomerHandler());

        Assert.Equal((supplier, true), (result.GetProperty("partyId").GetGuid(), result.GetProperty("existingParty").GetBoolean()));
        Assert.Equal((SalesErrors.CustomerExists, SalesErrors.NotDraft), (twice.Code, rename.Code));
        Assert.Equal("Constructora Uno:ACTIVE:true:DRAFT:809-555-0199:pagos@uno.do:3", await h.ScalarAsync<string>(
            $"SELECT legal_name || ':' || status::text || ':' || is_supplier || ':' || customer_status || ':' || phone || ':' || email || ':' || version FROM md.party WHERE party_id = '{supplier}'"));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListCustomers(h.CompanyId, a.Seller, Search: "uno"), new ListCustomersHandler())).RootElement.GetProperty("items");
        Assert.Equal((1, true), (list.GetArrayLength(), list[0].GetProperty("isSupplier").GetBoolean()));
    }

    [Fact]
    public async Task Terms_keep_one_draft_are_approved_by_another_person_and_the_new_version_supersedes_the_old()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var party = (await h.RunAsync(new CreateCustomer(h.CompanyId, a.Seller, "c", "131925332", "Constructora Uno"), new CreateCustomerHandler())).ResultRef;
        var first = await Terms(h, a, party, "t1");

        var draft = await Terms(h, a, party, "t2", 45, 150000.00m, approve: false);
        var replaced = await Terms(h, a, party, "t3", 60, 175000.50m, approve: false);
        var badLimit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PrepareCustomerTerms(h.CompanyId, a.Credit, "bad", party, 30, 1.005m, false), new PrepareCustomerTermsHandler()));
        var badDays = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PrepareCustomerTerms(h.CompanyId, a.Credit, "days", party, 400, 1m, false), new PrepareCustomerTermsHandler()));
        var creditApproves = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveCustomerTerms(h.CompanyId, a.Credit, "self", draft), new ApproveCustomerTermsHandler()));
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, a.Controller, "approve", draft), new ApproveCustomerTermsHandler());
        var again = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveCustomerTerms(h.CompanyId, a.Controller, "again", draft), new ApproveCustomerTermsHandler()));

        Assert.Equal(draft, replaced);
        Assert.Equal((SalesErrors.AmountInvalid, SalesErrors.FieldInvalid, AuthorizationErrors.NotAuthorized, SalesErrors.InvalidState), (badLimit.Code, badDays.Code, creditApproves.Code, again.Code));
        Assert.Equal($"1:SUPERSEDED:30|2:ACTIVE:60:175000.50", await h.ScalarAsync<string>(
            "SELECT string_agg(version || ':' || status || ':' || payment_terms_days || CASE WHEN status = 'ACTIVE' THEN ':' || credit_limit::numeric(19,2) ELSE '' END, '|' ORDER BY version) FROM sal.customer_terms_version"));
        var pending = JsonDocument.Parse(await h.QueryAsync(new ListCustomerTerms(h.CompanyId, a.Controller, "DRAFT"), new ListCustomerTermsHandler())).RootElement.GetProperty("items");
        Assert.Equal(0, pending.GetArrayLength());
        Assert.NotEqual(first, draft);
    }
}
