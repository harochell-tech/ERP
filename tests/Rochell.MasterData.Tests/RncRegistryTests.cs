using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rochell.MasterData.Queries;
using Rochell.MasterData.Rnc;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>E-RNC-1…8: the DGII registry file, its import and the lookups.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RncRegistryTests(PostgresFixture postgres)
{
    /// <summary>A slice of the DGII's format: Latin-1, CRLF, eleven fields; one name broken over two lines, one bad row, one repeat.</summary>
    private const string Sample =
        "131925332|BLOCK ROCHELL SRL|BLOCK ROCHELL|VENTA AL POR MAYOR DE LADRILLO| | | | |02/04/2019|ACTIVO|NORMAL\r\n" +
        "101000001|FERRETERÍA DOS SRL||FERRETERÍAS| | | | |15/09/1998|SUSPENDIDO|NORMAL\r\n" +
        "132156056|TIENDA D CARMEN HELENA Y CONFECCIONES SRL|TIENDA D CARMEN\r\n" +
        "|VENTA AL POR MAYOR DE PRENDAS | | | | |17/09/2020|ACTIVO|RST\r\n" +
        "12345|FILA MALA||| | | | ||ACTIVO|NORMAL\r\n" +
        "00112345678|JUAN PÉREZ||EMPLEADOS (ASALARIADOS) | | | | ||DADO DE BAJA|NORMAL\r\n" +
        "101000001|FERRETERÍA DOS, S.R.L.||FERRETERÍAS| | | | |15/09/1998|SUSPENDIDO|NORMAL\r\n";

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    [Fact]
    public void The_DGII_file_is_read_with_its_accents_broken_names_bad_rows_and_repeats()
    {
        var (entries, skipped) = RncRegistryFile.Parse(new MemoryStream(Latin1(Sample)));

        Assert.Equal(2, skipped); // the bad row and the repeated 101000001
        Assert.Equal(
            "00112345678:JUAN PÉREZ:DADO DE BAJA:|101000001:FERRETERÍA DOS, S.R.L.:SUSPENDIDO:1998-09-15|131925332:BLOCK ROCHELL SRL:ACTIVO:2019-04-02|132156056:TIENDA D CARMEN HELENA Y CONFECCIONES SRL:ACTIVO:2020-09-17",
            string.Join('|', entries.OrderBy(e => e.Rnc, StringComparer.Ordinal).Select(e => $"{e.Rnc}:{e.LegalName}:{e.Status}:{e.StartedOn:yyyy-MM-dd}")));
        Assert.Equal("TIENDA D CARMEN:VENTA AL POR MAYOR DE PRENDAS:RST", entries.Where(e => e.Rnc == "132156056").Select(e => $"{e.TradeName}:{e.Activity}:{e.Regime}").Single());
    }

    [Fact]
    public async Task An_import_replaces_the_registry_and_the_buyer_looks_up_a_number_and_the_discrepancies()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var folder = Directory.CreateTempSubdirectory();
        var zip = Path.Combine(folder.FullName, "DGII_RNC.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("TMP/DGII_RNC.TXT");
            await using var stream = entry.Open();
            await stream.WriteAsync(Latin1(Sample));
        }

        await using var owner = await h.Admin.OpenConnectionAsync();
        var stale = Path.Combine(folder.FullName, "old.txt");
        await File.WriteAllBytesAsync(stale, Latin1("999999999|VIEJO SRL||| | | | ||ACTIVO|NORMAL\r\n"));
        await RncRegistryFile.ImportAsync(owner, stale, "prueba", new DateOnly(2026, 9, 12), h.Clock.UtcNow, CancellationToken.None);
        var result = await RncRegistryFile.ImportAsync(owner, zip, "prueba", new DateOnly(2026, 9, 19), h.Clock.UtcNow, CancellationToken.None);

        await h.CreateActiveSupplierAsync("131925332", "BLOCK ROCHELL, S.R.L."); // same name but for punctuation: no discrepancy
        await h.CreateActiveSupplierAsync("101000001", "Ferretería Dos");         // SUSPENDIDO
        await h.CreateActiveSupplierAsync("132156056", "Otra Tienda");           // named otherwise
        await h.CreateActiveSupplierAsync("401000009", "Nueva Empresa");         // not in the registry
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");

        var found = JsonDocument.Parse(await h.QueryAsync(new GetRnc(h.CompanyId, buyer, "131-92533-2"), new GetRncHandler())).RootElement;
        var missing = JsonDocument.Parse(await h.QueryAsync(new GetRnc(h.CompanyId, buyer, "999999999"), new GetRncHandler())).RootElement;
        var status = JsonDocument.Parse(await h.QueryAsync(new GetRncRegistryStatus(h.CompanyId, buyer), new GetRncRegistryStatusHandler())).RootElement;
        var badFormat = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new GetRnc(h.CompanyId, buyer, "12345"), new GetRncHandler()));
        var denied = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new GetRnc(h.CompanyId, storekeeper, "131925332"), new GetRncHandler()));

        Assert.Equal((4, 2), (result.Rows, result.Skipped));
        Assert.Equal("true|BLOCK ROCHELL SRL|ACTIVO|2026-09-19", $"{found.GetProperty("found").GetBoolean().ToString().ToLowerInvariant()}|{found.GetProperty("legalName").GetString()}|{found.GetProperty("status").GetString()}|{found.GetProperty("registryDate").GetString()}");
        Assert.False(missing.GetProperty("found").GetBoolean()); // the older import was replaced
        Assert.Equal("2026-09-19|4", $"{status.GetProperty("lastImport").GetProperty("sourceDate").GetString()}|{status.GetProperty("lastImport").GetProperty("rows").GetInt32()}");
        Assert.Equal(
            "Ferretería Dos:NOT_ACTIVE:SUSPENDIDO|Nueva Empresa:NOT_FOUND:|Otra Tienda:NAME_DIFFERS:ACTIVO",
            string.Join('|', status.GetProperty("discrepancies").EnumerateArray().Select(d => $"{d.GetProperty("legalName").GetString()}:{d.GetProperty("issue").GetString()}:{d.GetProperty("registryStatus").GetString()}")));
        Assert.Equal((QueryErrors.InvalidParameter, AuthorizationErrors.NotAuthorized), (badFormat.Code, denied.Code));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM md.rnc_registry_import"));
    }
}
