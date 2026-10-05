using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>UX3-01 (E-UX3-2/3): the reconciliations and their findings in words, and readable match keys.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed partial class ReconciliationTextTests(PostgresFixture postgres)
{
    /// <summary>
    /// UPPER_SNAKE literals of Reconciliations.cs that are not classifications: account roles, document and line statuses, reports and
    /// rule kinds the SQL filters on. A new literal must be added here or be a classification with a rec.recon_classification row.
    /// </summary>
    private static readonly HashSet<string> NotClassifications = new(StringComparer.Ordinal)
    {
        "AP_CONTROL", "AR_CONTROL", "CASH_IN_TRANSIT", "CONTRACT_ASSET", "FINISHED_GOODS", "FINISHED_GOODS_IN_TRANSIT", "ITBIS_RECOVERABLE", "MANUAL_ADJUSTMENT",
        "MIGRATION_CLEARING", "RAW_MATERIAL", "UNAPPLIED_RECEIPTS", "UNBILLED_RECEIVABLE", "BALANCE_SHEET", "INCOME_STATEMENT", "CHARGE_RECOGNIZED", "IN_PROGRESS",
        "IN_TRANSIT", "NOT_POSTED", "PENDING_EXTERNAL", "POSTING_BLOCKED", "SALES_ITBIS",
        "SELECTIVE_TAX_EXPENSE", "OTHER_TAX_EXPENSE", "LEGAL_TIP_EXPENSE", // account roles TAX-606 reads (E-GAS-06-5)
        "AP_FOREIGN", // AP-GL counts foreign payables too (E-USD1-03-5)
    };

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rochell.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (Rochell.slnx) not found.");
    }

    [GeneratedRegex("'([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)'")]
    private static partial Regex SqlLiteral();

    [GeneratedRegex("\"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)\", \"(?:ERROR|WARNING)\"")]
    private static partial Regex FindingClassification();

    [GeneratedRegex("THEN '([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)' END")]
    private static partial Regex WarningLiteral();

    /// <summary>
    /// Every classification the sources can produce: the SQL literals of Reconciliations.cs (minus <see cref="NotClassifications"/>), the
    /// findings BankGl.cs builds in C#, and the warnings of tax.report_606 (TAX-606 unnests them), read from the migrated function.
    /// </summary>
    private static async Task<SortedSet<string>> ProducedClassificationsAsync(TestHarness h)
    {
        var source = Path.Combine(RepositoryRoot(), "src", "Rochell.Reconciliation");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        found.UnionWith(SqlLiteral().Matches(await File.ReadAllTextAsync(Path.Combine(source, "Reconciliations.cs"))).Select(m => m.Groups[1].Value).Where(v => !NotClassifications.Contains(v)));
        found.UnionWith(FindingClassification().Matches(await File.ReadAllTextAsync(Path.Combine(source, "BankGl.cs"))).Select(m => m.Groups[1].Value));
        var function = (await h.ScalarAsync<string>("SELECT pg_get_functiondef('tax.report_606(uuid, date)'::regprocedure)"))!;
        var warnings = function[function.IndexOf("array_remove(ARRAY[", StringComparison.Ordinal)..];
        found.UnionWith(WarningLiteral().Matches(warnings).Select(m => m.Groups[1].Value));
        return found;
    }

    [Fact]
    public async Task Every_reconciliation_and_every_classification_it_produces_has_its_Spanish_texts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var produced = await ProducedClassificationsAsync(h);
        var catalogued = new SortedSet<string>(StringComparer.Ordinal);
        await using (var command = h.Admin.CreateCommand("SELECT classification FROM rec.recon_classification WHERE length(btrim(name)) > 0 AND length(btrim(guidance)) > 0"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                catalogued.Add(reader.GetString(0));
            }
        }

        var definitions = JsonDocument.Parse(await h.QueryAsync(new ListReconciliationDefinitions(h.CompanyId, controller), new ListReconciliationDefinitionsHandler())).RootElement
            .GetProperty("items").EnumerateArray().ToList();

        Assert.Contains("CLASSIFICATION_MISSING", produced);   // from tax.report_606
        Assert.Contains("BANK_GL_DIFFERENCE", produced);       // from BankGl.cs
        Assert.Equal(66, produced.Count); // + the three of TAX-606 for expense taxes (E-GAS-06-5); + the three of CASH-SALE (E-CF1-10, 11); // + the three of PROFORMA-ASIG (E-FIS1b-01-12)
        Assert.Equal(produced, catalogued);                     // none missing, none stale
        Assert.Equal(Reconciliations.All.Order(StringComparer.Ordinal), definitions.Select(d => d.GetProperty("reconCode").GetString()));
        Assert.All(definitions, d => Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("name").GetString())));
        Assert.All(definitions, d => Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("guidance").GetString())));
        var evidence = definitions.Single(d => d.GetProperty("reconCode").GetString() == "ACC-EVIDENCE");
        Assert.Equal("AP-REC,AR-REC,BANK-REC,INV-MOV", string.Join(',', evidence.GetProperty("blockingComponents").EnumerateArray().Select(c => c.GetString())));
        Assert.Equal(("Evidencia contable de los documentos", "ERROR"), (evidence.GetProperty("name").GetString(), evidence.GetProperty("severity").GetString()));
        Assert.Empty(definitions.Single(d => d.GetProperty("reconCode").GetString() == "GRNI-AGING").GetProperty("blockingComponents").EnumerateArray());
    }

    [Fact]
    public async Task A_run_shows_names_guidance_and_readable_match_keys()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        await h.AdminRequireAsync("BEGIN; SET LOCAL session_replication_role = replica; UPDATE fin.ap_document SET open_amount = open_amount - 100; COMMIT;");
        var controller = s.Purchasing.Controller;
        var apDoc = await h.ScalarAsync<Guid>("SELECT ap_doc_id FROM fin.ap_document");

        var result = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "rec", ["AP-GL", "PAY-APPL"]), new RunReconciliationHandler())).ResultPayload).RootElement;
        var runs = result.GetProperty("runs").EnumerateArray().ToDictionary(r => r.GetProperty("code").GetString()!, r => r.GetProperty("runId").GetGuid());
        var apGl = JsonDocument.Parse(await h.QueryAsync(new GetReconciliationRun(h.CompanyId, controller, runs["AP-GL"]), new GetReconciliationRunHandler())).RootElement;
        var payAppl = JsonDocument.Parse(await h.QueryAsync(new GetReconciliationRun(h.CompanyId, controller, runs["PAY-APPL"]), new GetReconciliationRunHandler())).RootElement;
        var list = JsonDocument.Parse(await h.QueryAsync(new ListReconciliationRuns(h.CompanyId, controller, "AP-GL"), new ListReconciliationRunsHandler())).RootElement;

        // AP-GL: 10,520.00 open (10,620.00 − 100.00) against 10,620.00 in AP_CONTROL, keyed by the supplier's id.
        var difference = apGl.GetProperty("exceptions").EnumerateArray().Single();
        Assert.Equal(s.Purchasing.SupplierId.ToString(), difference.GetProperty("matchKey").GetString());
        Assert.Equal("Agregados del Este, S.R.L.", difference.GetProperty("matchLabel").GetString());
        Assert.Equal((10520m, 10620m), (decimal.Parse(difference.GetProperty("valueA").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(difference.GetProperty("valueB").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("Diferencia entre cuentas por pagar y su cuenta de control", difference.GetProperty("classificationName").GetString());
        Assert.StartsWith("El saldo abierto de las facturas del proveedor", difference.GetProperty("guidance").GetString(), StringComparison.Ordinal);
        Assert.Equal("Cuentas por pagar contra contabilidad", apGl.GetProperty("run").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(apGl.GetProperty("run").GetProperty("guidance").GetString()));
        Assert.Equal("Cuentas por pagar contra contabilidad", list.GetProperty("items")[0].GetProperty("name").GetString());

        // PAY-APPL: the AP document by the invoice's NCF and supplier.
        var document = payAppl.GetProperty("exceptions").EnumerateArray().Single();
        Assert.Equal($"ap_doc:{apDoc}", document.GetProperty("matchKey").GetString());
        Assert.Equal("B0100000001 · Agregados del Este, S.R.L.", document.GetProperty("matchLabel").GetString());
        Assert.Equal("AP_DOCUMENT_APPLICATION_DIFFERENCE", document.GetProperty("classification").GetString());
    }

    [Fact]
    public async Task Keys_of_other_shapes_resolve_and_unknown_ones_stay_null()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var controller = s.Purchasing.Controller;
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", s.Purchasing.PlantId));
        var areaCode = await h.ScalarAsync<string>("SELECT code FROM md.valuation_area WHERE valuation_area_id = @a", ("a", area));
        var grNo = await h.ScalarAsync<string>("SELECT gr_no FROM pur.goods_receipt");
        var poNo = await h.ScalarAsync<string>("SELECT po_no FROM pur.purchase_order WHERE po_id = @p", ("p", s.PurchaseOrderId));
        var (connection, transaction) = await h.OpenAppTransactionAsync();
        await using var disposeConnection = connection;
        await using var disposeTransaction = transaction;
        var context = new Rochell.Platform.Queries.QueryContext(connection, transaction, h.CompanyId, controller, h.Clock);

        var inventory = await MatchLabels.ResolveAsync(context, "INV-VALUE-GL", [$"{area}/{s.Purchasing.Sand}"], CancellationToken.None);
        var evidence = await MatchLabels.ResolveAsync(context, "ACC-EVIDENCE", [$"GR:{s.GoodsReceiptId}", "PAY:PAG-000001", "journal:x"], CancellationToken.None);
        var grni = await MatchLabels.ResolveAsync(context, "GRNI-AGING", [s.PoLineId.ToString()], CancellationToken.None);

        Assert.Equal($"{areaCode} / ARENA-LAVADA", inventory[$"{area}/{s.Purchasing.Sand}"]);
        Assert.Equal(grNo, evidence[$"GR:{s.GoodsReceiptId}"]);
        Assert.Null(evidence["PAY:PAG-000001"]);
        Assert.Null(evidence["journal:x"]);
        Assert.Equal($"{poNo} línea 1 · ARENA-LAVADA", grni[s.PoLineId.ToString()]);
        await transaction.RollbackAsync();
    }
}
