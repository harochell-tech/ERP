using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Finance.Ledger;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.Queries;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// USD1-03 (USD-02, USD-03, E-USD1-03-1…8): the foreign supplier's expense order and invoice in USD — no NCF, tax type, ITBIS or
/// withholding; the invoice takes the rate of its date, its lines go to their categories' accounts (an expense, or a fixed asset with 606
/// type 04) and the payable to «Proveedores del exterior» with its USD amount (P-38).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ForeignInvoiceTests(PostgresFixture postgres)
{
    private const string P38 = "0192f001-0000-7000-8000-000000000030";

    internal sealed record Foreign(World W, Guid Supplier, Guid Forklift, Guid Parts);

    /// <summary>The expense world, a foreign supplier, today's rate, AP_FOREIGN mapped, P-38 approved and a fixed-asset category.</summary>
    internal static async Task<Foreign> ForeignAsync(TestHarness h, decimal rate = 60m)
    {
        var w = await WorldAsync(h);
        var supplier = (await h.RunAsync(
            new Rochell.MasterData.Suppliers.CreateForeignSupplier(h.CompanyId, w.S.Purchasing.Buyer, "sup-c", "Forklift Parts Inc.", "us", "EIN 12-3456789"),
            new Rochell.MasterData.Suppliers.CreateForeignSupplierHandler())).ResultRef;
        await h.RunAsync(new Rochell.MasterData.Suppliers.ActivateSupplier(h.CompanyId, w.Controller, "sup", supplier, 1), new Rochell.MasterData.Suppliers.ActivateSupplierHandler());

        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var prepared = await h.RunAsync(new PrepareExchangeRate(h.CompanyId, treasurer, "rate", "USD", Today(h), rate, "Banco Central"), new PrepareExchangeRateHandler());
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, w.Controller, "rate-a", prepared.ResultRef, 1), new ApproveExchangeRateHandler());

        await h.CreateActiveMapAsync("AP_FOREIGN", await h.CreateAccountAsync("21020", "Proveedores del exterior", isControl: true));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P38}' AND version = 1");

        // E-USD1-03-6: a fixed-asset category (606 type 04).
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var asset = (await h.RunAsync(new CreateAccount(h.CompanyId, w.Controller, "acc-mont", "15300", "Montacargas y equipos", "ASSET", false), new CreateAccountHandler())).ResultRef;
        var forklift = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, contador, "cat-mont", "MONTACARGAS", "Montacargas y equipos", asset, "04", "GOODS"), new PrepareExpenseCategoryHandler()))
            .ResultRef;
        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, w.Controller, "cat-mont-a", [forklift]), new ApproveExpenseCategoriesHandler());
        return new Foreign(w, supplier, forklift, w.Categories["REPUESTOS"]);
    }

    internal static ExpenseLineInput Usd(Guid category, string description, decimal qty, decimal price, Guid? poLine = null) => new(description, category, null, qty, price, poLine);

    private static Task<CommandResult> RegisterAsync(TestHarness h, Foreign f, string key, string number, Guid? po, params ExpenseLineInput[] lines)
        => h.RunAsync(
            new RegisterExpenseInvoice(h.CompanyId, f.W.Clerk, key, f.Supplier, number, Today(h), Today(h).AddDays(30), f.W.Plant, lines, PurchaseOrderId: po),
            new RegisterExpenseInvoiceHandler());

    private static async Task<JsonElement> RunAsync<T>(TestHarness h, T command, ICommandHandler<T> handler)
        where T : ICommand
        => JsonDocument.Parse((await h.RunAsync(command, handler)).ResultPayload).RootElement;

    /// <summary>Each account P-38 touched (code:debit − credit) | the AP_FOREIGN lines' USD amounts | the AP document in pesos and USD.</summary>
    private static Task<string?> BooksAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT (SELECT string_agg(a.code || ':' || x.amount, ',' ORDER BY a.code)
                    FROM (SELECT account_id, sum(debit - credit)::numeric(19,2) AS amount FROM fin.gl_entry WHERE rule_line_code LIKE 'P38-%' GROUP BY account_id) x JOIN fin.account a ON a.account_id = x.account_id)
                   || '|' || coalesce((SELECT string_agg(currency || ' ' || amount_fc::numeric(19,2), ',' ORDER BY line_no) FROM fin.gl_entry WHERE account_role = 'AP_FOREIGN'), '-')
                   || '|' || coalesce((SELECT string_agg(currency || ' ' || original_amount::numeric(19,2) || '/' || open_amount::numeric(19,2) || ' USD ' || original_amount_fc::numeric(19,2)
                                                         || '/' || open_amount_fc::numeric(19,2), ',') FROM fin.ap_document), '-')
            """);

    [Trait("AcceptanceUsd1", "USD-03")]
    [Fact]
    public async Task USD03_a_foreign_invoice_of_USD_10000_posts_600000_pesos_to_the_asset_and_the_parts_against_foreign_payables()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var f = await ForeignAsync(h);
        var registered = JsonDocument.Parse((await RegisterAsync(
            h, f, "r", "INV-2026-0147", null, Usd(f.Forklift, "Montacargas usado Toyota 8FGU25", 1m, 8000m), Usd(f.Parts, "Repuestos de montacargas", 4m, 500m))).ResultPayload).RootElement;
        var si = registered.GetProperty("supplierInvoiceId").GetGuid();

        // 600,000.00 pesos is above the approval amount of an invoice without order (25,000.00): the Controller approves it.
        var match = await RunAsync(h, new MatchSupplierInvoice(h.CompanyId, f.W.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new ApproveMatchException(h.CompanyId, f.W.Controller, "a", si, 2, "Montacargas aprobado por el dueño"), new ApproveMatchExceptionHandler());
        var posted = await RunAsync(h, new PostSupplierInvoice(h.CompanyId, f.W.Clerk, "p", si, 3), new PostSupplierInvoiceHandler());
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSupplierInvoice(h.CompanyId, f.W.Clerk, si), new GetSupplierInvoiceHandler())).RootElement;
        var report606 = await h.ScalarAsync<long>($"SELECT count(*) FROM tax.report_606('{h.CompanyId}', CURRENT_DATE) WHERE si_id = '{si}'");

        Assert.Equal("USD|60.0000|600000.00|10000.00", $"{registered.GetProperty("currency").GetString()}|{registered.GetProperty("exchangeRate").GetString()}|" +
            $"{registered.GetProperty("totalAmount").GetString()}|{registered.GetProperty("totalAmountUsd").GetString()}");
        Assert.Equal("MATCH_EXCEPTION|600000.00", $"{match.GetProperty("status").GetString()}|{match.GetProperty("total").GetString()}");
        Assert.Equal("POSTED|600000.00|10000.00", $"{posted.GetProperty("accountingStatus").GetString()}|{posted.GetProperty("payable").GetString()}|{posted.GetProperty("payableUsd").GetString()}");
        Assert.Equal("15300:480000.00,21020:-600000.00,66150:120000.00|USD 10000.00|USD 600000.00/600000.00 USD 10000.00/10000.00", await BooksAsync(h));
        Assert.Equal(
            "USD|0.0000|600000.0000|10000.0000|8000.000000:480000.0000,500.000000:120000.0000|-",
            $"{detail.GetProperty("currency").GetString()}|{detail.GetProperty("itbisTotal").GetString()}|{detail.GetProperty("grossTotal").GetString()}|" +
            $"{detail.GetProperty("totalAmountUsd").GetString()}|" +
            string.Join(',', detail.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("unitPriceUsd").GetString()}:{l.GetProperty("netAmount").GetString()}")) + "|" +
            (detail.GetProperty("lines")[0].TryGetProperty("taxTypeId", out var tax) && tax.ValueKind != JsonValueKind.Null ? "?" : "-"));
        Assert.Equal(0, report606); // E-USD1-03-7: no NCF, not in the 606
        Assert.Equal("P-38:P38-DR-EXP,P38-DR-EXP,P38-CR-AP", await h.ScalarAsync<string>(
            "SELECT r.code || ':' || string_agg(e.rule_line_code, ',' ORDER BY e.line_no) FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id JOIN fin.gl_entry e ON e.journal_id = j.journal_id GROUP BY r.code"));

        // The USD amount is part of the ledger row's hash (E-USD1-01-3).
        await using (var command = h.Admin.CreateCommand(
            """
            SELECT gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, currency, plant_id, item_id, party_id,
                   subledger_type, subledger_ref, inv_value_entry_id, source_event_id, rule_line_code, determination_inputs::text, amount_fc, row_hash
            FROM fin.gl_entry WHERE account_role = 'AP_FOREIGN'
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            var row = PostingEngine.ReadEntry(reader, 19);
            Assert.Equal((byte[])reader.GetValue(20), row.ComputeRowHash());
            Assert.NotEqual((byte[])reader.GetValue(20), (row with { AmountFc = 10001m }).ComputeRowHash());
        }

        // Reversed while unpaid: the books return to zero and the AP document closes in both currencies.
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, f.W.Controller, "x", si, 4, "Factura duplicada"), new ReverseSupplierInvoiceHandler());
        Assert.Equal("15300:0.00,21020:0.00,66150:0.00|USD 10000.00,USD 10000.00|USD 600000.00/0.00 USD 10000.00/0.00", await BooksAsync(h));
    }

    [Fact]
    public async Task The_rounding_cent_goes_to_the_largest_line_and_a_document_without_its_days_rate_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var f = await ForeignAsync(h, rate: 60.1234m);

        // 10.01 × 60.1234 = 601.84 per line, 1,805.52 in all; but 30.03 × 60.1234 = 1,805.51: the first (largest) line takes −0.01.
        var si = (await RegisterAsync(h, f, "r", "A-1", null, Usd(f.Parts, "Filtro", 1m, 10.01m), Usd(f.Parts, "Correa", 1m, 10.01m), Usd(f.Parts, "Sello", 1m, 10.01m))).ResultRef;
        var noRate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterExpenseInvoice(h.CompanyId, f.W.Clerk, "old", f.Supplier, "A-2", Today(h).AddDays(-60), Today(h), f.W.Plant, [Usd(f.Parts, "Filtro", 1m, 10m)]),
            new RegisterExpenseInvoiceHandler()));

        Assert.Equal(
            "1805.51|30.03|601.83,601.84,601.84",
            await h.ScalarAsync<string>(
                $"""
                SELECT total_amount::numeric(19,2) || '|' || total_amount_fc::numeric(19,2) || '|' ||
                       (SELECT string_agg(net_amount::numeric(19,2)::text, ',' ORDER BY line_no) FROM pur.supplier_invoice_line WHERE si_id = '{si}')
                FROM pur.supplier_invoice WHERE si_id = '{si}'
                """));
        Assert.Equal(ExchangeRateErrors.Missing, noRate.Code);
    }

    [Trait("AcceptanceUsd1", "USD-02")]
    [Fact]
    public async Task USD02_an_order_in_USD_is_approved_on_its_peso_value_and_billed_at_its_USD_price()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var f = await ForeignAsync(h);
        var p = f.W.S.Purchasing;
        var po = (await h.RunAsync(
            new CreateExpensePurchaseOrder(h.CompanyId, p.Buyer, "po", p.PlantId, f.Supplier, Today(h), [new ExpenseOrderLineInput("Montacargas usado", f.Forklift, null, 1m, 8000m)]),
            new CreateExpensePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, "po-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());

        // USD 8,000.00 × 60 = 480,000.00 pesos: above the purchasing approver's limit (100,000.00); the Controller approves it.
        var limited = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Approver, "po-a1", p.PlantId, po, 2), new ApprovePurchaseOrderHandler()));
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, "po-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        var line = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
        var order = JsonDocument.Parse(await h.QueryAsync(new GetPurchaseOrder(h.CompanyId, p.Buyer, po, p.PlantId), new GetPurchaseOrderHandler())).RootElement;

        var si = (await RegisterAsync(h, f, "r", "INV-77", po, Usd(f.Forklift, "Montacargas usado", 1m, 8000m, line))).ResultRef;
        var match = await RunAsync(h, new MatchSupplierInvoice(h.CompanyId, f.W.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, f.W.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());

        Assert.Equal(ProcurementErrors.ApprovalLimitExceeded, limited.Code);
        Assert.Contains("480000.00", limited.Message, StringComparison.Ordinal);
        Assert.Equal("USD|8000.00", $"{order.GetProperty("currency").GetString()}|{order.GetProperty("total").GetString()}");
        Assert.Equal("MATCHED", match.GetProperty("status").GetString());
        Assert.Equal("CLOSED", await h.ScalarAsync<string>("SELECT status::text FROM pur.purchase_order WHERE po_id = @p", ("p", po)));
        Assert.Equal("15300:480000.00,21020:-480000.00|USD 8000.00|USD 480000.00/480000.00 USD 8000.00/8000.00", await BooksAsync(h));
    }

    [Fact]
    public async Task A_foreign_supplier_buys_only_expenses_without_tax_type_and_a_local_line_always_has_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var f = await ForeignAsync(h);
        var p = f.W.S.Purchasing;

        var withType = await Assert.ThrowsAsync<DomainException>(() => RegisterAsync(
            h, f, "t", "INV-1", null, new ExpenseLineInput("Repuestos", f.Parts, f.W.Types["ITBIS_18"], 1m, 10m)));
        var localWithout = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterExpenseInvoice(h.CompanyId, f.W.Clerk, "l", p.SupplierId, "B0100000901", Today(h), Today(h), f.W.Plant, [Usd(f.Parts, "Repuestos", 1m, 10m)]),
            new RegisterExpenseInvoiceHandler()));
        var inventory = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreatePurchaseOrder(h.CompanyId, p.Buyer, "inv", p.PlantId, f.Supplier, Today(h), [new PurchaseOrderLineInput(p.Sand, "M3", 1m, 10m)]),
            new CreatePurchaseOrderHandler()));
        var longNumber = await Assert.ThrowsAsync<DomainException>(() => RegisterAsync(h, f, "n", new string('9', 41), null, Usd(f.Parts, "Repuestos", 1m, 10m)));

        Assert.Equal(
            (ExpenseErrors.TaxTypeCurrency, ExpenseErrors.TaxTypeCurrency, ExpenseErrors.ForeignSupplierExpensesOnly, ExpenseErrors.ForeignNumberInvalid),
            (withType.Code, localWithout.Code, inventory.Code, longNumber.Code));
    }

    [Fact]
    public async Task A_fixed_asset_category_needs_606_type_04()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var asset = (await h.RunAsync(new CreateAccount(h.CompanyId, w.Controller, "acc-veh", "15400", "Vehículos pesados", "ASSET", false), new CreateAccountHandler())).ResultRef;

        var wrongType = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareExpenseCategory(h.CompanyId, contador, "c1", "VEHICULOS", "Vehículos pesados", asset, "09", "GOODS"), new PrepareExpenseCategoryHandler()));
        var category = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, contador, "c2", "VEHICULOS", "Vehículos pesados", asset, "04", "GOODS"), new PrepareExpenseCategoryHandler()))
            .ResultRef;
        var retype = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateExpenseCategoryDraft(h.CompanyId, contador, "c3", category, 1, "Vehículos pesados", "09", "GOODS"), new UpdateExpenseCategoryDraftHandler()));

        Assert.Equal((ExpenseErrors.AccountNotExpense, ExpenseErrors.CategoryInvalid), (wrongType.Code, retype.Code));
    }

    [Fact]
    public async Task A_foreign_supplier_is_created_with_its_country_corrected_while_draft_and_unique_per_tax_id()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var buyer = p.Buyer;
        var created = await h.RunAsync(new Rochell.MasterData.Suppliers.CreateForeignSupplier(h.CompanyId, buyer, "c1", "Besser Company", "us", "EIN 1"), new Rochell.MasterData.Suppliers.CreateForeignSupplierHandler());
        var badCountry = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new Rochell.MasterData.Suppliers.CreateForeignSupplier(h.CompanyId, buyer, "c2", "Otro", "USA"), new Rochell.MasterData.Suppliers.CreateForeignSupplierHandler()));
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new Rochell.MasterData.Suppliers.CreateForeignSupplier(h.CompanyId, buyer, "c3", "Besser Co.", "US", "EIN 1"), new Rochell.MasterData.Suppliers.CreateForeignSupplierHandler()));
        await h.RunAsync(
            new Rochell.MasterData.Suppliers.UpdateForeignSupplierDraft(h.CompanyId, buyer, "u1", created.ResultRef, 1, "Besser Company LLC", "US", null),
            new Rochell.MasterData.Suppliers.UpdateForeignSupplierDraftHandler());
        var asLocal = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new Rochell.MasterData.Suppliers.UpdateSupplier(h.CompanyId, buyer, "u2", created.ResultRef, 2, "101000000", "Besser"), new Rochell.MasterData.Suppliers.UpdateSupplierHandler()));

        Assert.Equal(
            (Rochell.MasterData.MasterDataErrors.CountryInvalid, Rochell.MasterData.MasterDataErrors.ForeignTaxIdDuplicate, Rochell.MasterData.MasterDataErrors.SupplierKindMismatch),
            (badCountry.Code, duplicate.Code, asLocal.Code));
        Assert.Equal(
            "FOREIGN|-|Besser Company LLC|US|-|DRAFT|2",
            await h.ScalarAsync<string>(
                "SELECT party_kind || '|' || coalesce(rnc, '-') || '|' || legal_name || '|' || country || '|' || coalesce(foreign_tax_id, '-') || '|' || status::text || '|' || version FROM md.party WHERE party_id = @p",
                ("p", created.ResultRef)));
    }
}
