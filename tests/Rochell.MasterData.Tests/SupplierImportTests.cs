using System.Text;
using System.Text.Json;
using Rochell.MasterData.Import;
using Rochell.MasterData.Queries;
using Rochell.MasterData.Rnc;
using Rochell.MasterData.Suppliers;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>IMP-01: the supplier file, its preview and import, activation of several suppliers and their contact data (E-IMP-1…11).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierImportTests(PostgresFixture postgres)
{
    private static readonly string?[] Header = ["Razón Social", "Teléfono 1", "Correo Electrónico", "Nombre Comercial", "Término de Pago", "ID Fiscal", "Moneda"];

    /// <summary>Synthetic rows in the shape of the ADM Cloud export (E-IMP-11).</summary>
    private static string File() => TestSpreadsheet.Base64(
        Header,
        ["Cementos Uno SRL", "809-555-0101", "compras@uno.test; Pagos@uno.test; compras@uno.test", null, "30 días", "1-01-00001-1", "DOP"],
        ["Sin Registro SRL", null, null, null, "Contado", "401000009", "DOP"],
        ["Cementos Uno (repetido)", null, null, null, null, "101000011", "DOP"],
        ["Extranjero LTD", null, null, null, null, null, "USD"],
        ["Ocho Dígitos LTD", null, null, null, null, "01609365", null],
        ["Ya Existe SRL", null, null, null, null, "130000001", "DOP"],
        ["Término Raro SRL", null, null, null, "A convenir", "101000029", "DOP"],
        ["Correo Malo SRL", null, "no-es-correo", null, null, "101000037", "DOP"]);

    private static async Task SeedRegistryAsync(TestHarness h)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "registry.txt");
        await System.IO.File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes("101000011|CEMENTOS UNO DEL ESTE SRL||CEMENTO| | | | |15/09/1998|ACTIVO|NORMAL\r\n"));
        await using var owner = await h.Admin.OpenConnectionAsync();
        await RncRegistryFile.ImportAsync(owner, path, "prueba", new DateOnly(2026, 9, 19), h.Clock.UtcNow, CancellationToken.None);
    }

    private static string Outcomes(JsonElement report)
        => string.Join('|', report.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("row").GetInt32()}:{i.GetProperty("outcome").GetString()}:{i.GetProperty("reasonCode").GetString()}"));

    [Fact]
    public void A_workbook_and_a_csv_file_are_read_as_rows_of_text()
    {
        var workbook = SpreadsheetFile.Read(TestSpreadsheet.Xlsx(["Razón Social", "ID Fiscal"], ["  Uno\tSRL ", null], [null, "101000011"]));
        var csv = SpreadsheetFile.Read(Encoding.UTF8.GetBytes("﻿Razón Social;ID Fiscal;Correo\r\n\"Dos; y \"\"Tres\"\" SRL\";101-00001-1;a@dos.test\r\n\r\n"));

        Assert.Equal("Razón Social,ID Fiscal|Uno SRL|,101000011", string.Join('|', workbook.Select(r => string.Join(',', r))));
        Assert.Equal("Razón Social,ID Fiscal,Correo|Dos; y \"Tres\" SRL,101-00001-1,a@dos.test", string.Join('|', csv.Select(r => string.Join(',', r))));
        Assert.Throws<SpreadsheetException>(() => SpreadsheetFile.Read([0x50, 0x4B, 0x03, 0x04, 0x00]));
    }

    [Fact]
    public async Task The_preview_says_what_each_row_would_do_and_the_import_does_exactly_that()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await SeedRegistryAsync(h);
        await h.CreateActiveSupplierAsync("130000001", "Ya Existe SRL");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");

        var preview = JsonDocument.Parse(await h.QueryAsync(new PreviewSupplierImport(h.CompanyId, buyer, "Proveedores.xlsx", File()), new PreviewSupplierImportHandler())).RootElement;
        var before = await h.ScalarAsync<long>("SELECT count(*) FROM md.party");
        var denied = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ImportSuppliers(h.CompanyId, storekeeper, "denied", "Proveedores.xlsx", File()), new ImportSuppliersHandler()));
        var imported = await h.RunAsync(new ImportSuppliers(h.CompanyId, buyer, "import", "Proveedores.xlsx", File()), new ImportSuppliersHandler());
        var report = JsonDocument.Parse(imported.ResultPayload).RootElement;

        const string Expected = "2:CREATE:|3:CREATE:|4:DUPLICATE:DUPLICATE_IN_FILE|5:REJECTED:ID_MISSING|6:REJECTED:ID_INVALID|7:EXISTS:ALREADY_SUPPLIER|8:REJECTED:TERMS_INVALID|9:REJECTED:EMAIL_INVALID";
        Assert.Equal(Expected, Outcomes(preview));
        Assert.Equal(Expected, Outcomes(report));
        Assert.Equal((1L, AuthorizationErrors.NotAuthorized), (before, denied.Code));
        Assert.Equal(
            (8, 2, 1, 1, 4, "Nombre Comercial,Moneda"),
            (report.GetProperty("rows").GetInt32(), report.GetProperty("toCreate").GetInt32(), report.GetProperty("existing").GetInt32(), report.GetProperty("duplicates").GetInt32(),
             report.GetProperty("rejected").GetInt32(), string.Join(',', report.GetProperty("ignoredColumns").EnumerateArray().Select(c => c.GetString()))));

        // E-IMP-3: the registry's legal name; E-IMP-6: every e-mail once, the first is the principal one; E-IMP-7: DRAFT.
        var first = report.GetProperty("items")[0];
        Assert.Equal(("CEMENTOS UNO DEL ESTE SRL", true, "ACTIVO"), (first.GetProperty("legalName").GetString(), first.GetProperty("nameDiffers").GetBoolean(), first.GetProperty("registryStatus").GetString()));
        Assert.Equal(
            "101000011|CEMENTOS UNO DEL ESTE SRL|DRAFT|809-555-0101|compras@uno.test|30|compras@uno.test,Pagos@uno.test",
            await h.ScalarAsync<string>(
                """
                SELECT concat_ws('|', p.rnc, p.legal_name, p.status, p.phone, p.email, p.supplier_payment_terms_days,
                                 (SELECT string_agg(e.email, ',' ORDER BY e.position) FROM md.party_email e WHERE e.party_id = p.party_id))
                FROM md.party p WHERE p.party_id = @p
                """,
                ("p", first.GetProperty("partyId").GetGuid())));
        Assert.Equal(
            "Sin Registro SRL|DRAFT|0|true",
            await h.ScalarAsync<string>(
                "SELECT concat_ws('|', legal_name, status, supplier_payment_terms_days, (phone IS NULL AND email IS NULL)::text) FROM md.party WHERE rnc = '401000009'"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'Party' AND to_state = 'DRAFT' AND command = 'MasterData.ImportSuppliers'"));
        Assert.Equal(
            report.GetProperty("sha256").GetString(),
            await h.ScalarAsync<string>("SELECT payload->>'sha256' FROM core.domain_event WHERE event_type = 'SuppliersImported' AND aggregate_id = @id", ("id", imported.ResultRef)));

        // E-IMP-5: the same file again loads nothing.
        var again = JsonDocument.Parse((await h.RunAsync(new ImportSuppliers(h.CompanyId, buyer, "import-2", "Proveedores.xlsx", File()), new ImportSuppliersHandler())).ResultPayload).RootElement;
        Assert.Equal((0, 3, 3L), (again.GetProperty("toCreate").GetInt32(), again.GetProperty("existing").GetInt32(), await h.ScalarAsync<long>("SELECT count(*) FROM md.party")));
    }

    [Theory]
    [InlineData("", "UmF6w7NuIFNvY2lhbA==")]
    [InlineData("a.xlsx", "not base64")]
    [InlineData("a.xlsx", "")]
    [InlineData("a.csv", "Tm9tYnJlLFJOQwpVbm8sMTAxMDAwMDEx")] // "Nombre,RNC": not the expected columns
    [InlineData("a.csv", "UmF6w7NuIFNvY2lhbCxJRCBGaXNjYWwK")] // the header alone
    public async Task A_file_that_is_not_the_export_is_refused_whole(string fileName, string content)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var preview = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new PreviewSupplierImport(h.CompanyId, buyer, fileName, content), new PreviewSupplierImportHandler()));
        var import = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ImportSuppliers(h.CompanyId, buyer, "bad", fileName, content), new ImportSuppliersHandler()));

        Assert.Equal((MasterDataErrors.ImportFileInvalid, MasterDataErrors.ImportFileInvalid), (preview.Code, import.Code));
    }

    [Fact]
    public async Task Several_suppliers_are_activated_at_once_by_someone_who_did_not_import_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var active = await h.CreateActiveSupplierAsync("130000001", "Ya Existe SRL");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var report = JsonDocument.Parse((await h.RunAsync(new ImportSuppliers(h.CompanyId, buyer, "import", "Proveedores.xlsx", File()), new ImportSuppliersHandler())).ResultPayload).RootElement;
        var created = report.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("outcome").GetString() == "CREATE").Select(i => i.GetProperty("partyId").GetGuid()).ToList();
        var unknown = Guid.CreateVersion7();

        var byImporter = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateSuppliers(h.CompanyId, buyer, "own", created), new ActivateSuppliersHandler()));
        var empty = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateSuppliers(h.CompanyId, controller, "empty", []), new ActivateSuppliersHandler()));
        var result = JsonDocument.Parse((await h.RunAsync(new ActivateSuppliers(h.CompanyId, controller, "activate", [.. created, active, unknown]), new ActivateSuppliersHandler())).ResultPayload).RootElement;

        Assert.Equal((AuthorizationErrors.NotAuthorized, MasterDataErrors.BatchInvalid), (byImporter.Code, empty.Code));
        Assert.Equal((4, 2, 2), (result.GetProperty("requested").GetInt32(), result.GetProperty("activated").GetInt32(), result.GetProperty("skipped").GetInt32()));
        Assert.Equal(
            $"{active}:NOT_DRAFT|{unknown}:NOT_FOUND",
            string.Join('|', result.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("outcome").GetString() == "SKIPPED")
                .Select(i => $"{i.GetProperty("partyId").GetGuid()}:{i.GetProperty("code").GetString()}").Order(StringComparer.Ordinal)));
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM md.party WHERE is_supplier AND status = 'ACTIVE'"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'Party' AND to_state = 'ACTIVE' AND command = 'MasterData.ActivateSuppliers'"));
    }

    [Fact]
    public async Task The_buyer_keeps_a_suppliers_phone_and_e_mails()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var supplier = await h.CreateActiveSupplierAsync("130000001", "Ya Existe SRL");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var version = await h.ScalarAsync<long>("SELECT version FROM md.party WHERE party_id = @p", ("p", supplier));

        await h.RunAsync(new SetSupplierContact(h.CompanyId, buyer, "c1", supplier, version, " 809-555-0199 ", ["ventas@existe.test", "cobros@existe.test", "VENTAS@existe.test"]), new SetSupplierContactHandler());
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetSupplierContact(h.CompanyId, buyer, "c2", supplier, version, null, []), new SetSupplierContactHandler()));
        var invalid = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetSupplierContact(h.CompanyId, buyer, "c3", supplier, version + 1, null, ["sin-arroba"]), new SetSupplierContactHandler()));
        var tooMany = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SetSupplierContact(h.CompanyId, buyer, "c4", supplier, version + 1, null, [.. Enumerable.Range(1, 11).Select(i => $"c{i}@existe.test")]), new SetSupplierContactHandler()));
        var listed = JsonDocument.Parse(await h.QueryAsync(new ListSuppliers(h.CompanyId, buyer), new ListSuppliersHandler())).RootElement.GetProperty("items")[0];

        Assert.Equal((MasterDataErrors.VersionConflict, MasterDataErrors.FieldInvalid, MasterDataErrors.FieldInvalid), (stale.Code, invalid.Code, tooMany.Code));
        Assert.Equal(
            ("809-555-0199", "ventas@existe.test,cobros@existe.test", version + 1),
            (listed.GetProperty("phone").GetString(), string.Join(',', listed.GetProperty("emails").EnumerateArray().Select(e => e.GetString())), listed.GetProperty("version").GetInt64()));
        Assert.Equal("ventas@existe.test", await h.ScalarAsync<string>("SELECT email FROM md.party WHERE party_id = @p", ("p", supplier)));

        await h.RunAsync(new SetSupplierContact(h.CompanyId, buyer, "c5", supplier, version + 1, null, null), new SetSupplierContactHandler());
        Assert.True(await h.ScalarAsync<bool>("SELECT phone IS NULL AND email IS NULL AND NOT EXISTS (SELECT 1 FROM md.party_email e WHERE e.party_id = p.party_id) FROM md.party p WHERE party_id = @p", ("p", supplier)));
    }
}
