using System.Net;
using Rochell.Finance.Ledger;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>FIN1-03 over HTTP: the trial balance as JSON and as a CSV file (E-FIN1-03-10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LedgerReportApiTests(PostgresFixture postgres)
{
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    [Fact]
    public async Task The_trial_balance_is_served_as_JSON_and_as_a_UTF8_CSV_file()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        await h.OpenPeriodsAsync(today.Year);
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        using var api = new ApiHost(h);
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var cash = (await controller.OkAsync(h.CompanyId, "finance", "create-account", new { code = "1100", name = "Caja", accountClass = "ASSET", isControl = false })).GetProperty("resultRef").GetGuid();
        var sales = (await controller.OkAsync(h.CompanyId, "finance", "create-account", new { code = "4100", name = "Ventas, contado", accountClass = "REVENUE", isControl = false })).GetProperty("resultRef").GetGuid();
        var id = (await h.RunAsync(
            new PrepareManualJournal(h.CompanyId, contador, "aj", today, "Venta", "Factura 1", Support, "ACR-NTX", false, [new(cash, 500.00m, 0m), new(sales, 0m, 500.00m)]),
            new PrepareManualJournalHandler())).ResultRef;
        await h.RunAsync(new SubmitManualJournal(h.CompanyId, contador, "aj-s", id, 1), new SubmitManualJournalHandler());
        await controller.OkAsync(h.CompanyId, "finance", "approve-manual-journal", new { manualJournalId = id, expectedVersion = 2 });
        var path = $"/api/v1/companies/{h.CompanyId}/finance/trial-balance?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        var json = await controller.GetOkAsync(path);
        using var csv = await controller.GetAsync(path + "&format=csv");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        var (badStatus, badCode) = await (await controller.GetAsync(path + "&format=xlsx")).ProblemAsync();

        Assert.True(json.GetProperty("balanced").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"balanza-{today:yyyyMMdd}-{today:yyyyMMdd}.csv", csv.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(
            "Código,Cuenta,Clase,Saldo inicial,Débitos,Créditos,Saldo final\r\n1100,Caja,ASSET,0.00,500.00,0.00,500.00\r\n4100,\"Ventas, contado\",REVENUE,0.00,0.00,500.00,-500.00\r\n,Totales,,0.00,500.00,500.00,0.00\r\n",
            System.Text.Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_PARAMETER"), (badStatus, badCode));
    }
}
