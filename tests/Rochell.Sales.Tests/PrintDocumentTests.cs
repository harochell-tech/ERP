using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Orders;
using Rochell.Sales.Printing;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// PRT-01 (E-PRT-1, E-PRT-01-1…4, 7): a document prints from the server with the company's ACTIVE format or the built-in one; the
/// conduce carries the driver's QR only on the screen (with its address), not in the e-mail.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PrintDocumentTests(PostgresFixture postgres)
{
    private static readonly DriverLinkKey Key = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private static async Task<JsonElement> PrintAsync(TestHarness h, Guid session, string type, Guid id, string? baseUrl = null)
        => JsonDocument.Parse(await h.QueryAsync(new GetPrintDocument(h.CompanyId, session, type, id, BaseUrl: baseUrl), new GetPrintDocumentHandler(Key))).RootElement;

    [Fact]
    public async Task The_conduce_prints_with_the_built_in_format_and_the_drivers_qr_only_on_screen()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (order, line) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 600m);
        var planned = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "plan", order, [new(line, 100m)]), new PlanDeliveryHandler())).ResultRef;
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, s, order, line, 500m, own: true, "d");

        var draft = await PrintAsync(h, s.Dispatch, PrintDocumentTypes.DeliveryNote, planned);
        var screen = await PrintAsync(h, s.Dispatch, PrintDocumentTypes.DeliveryNote, delivery, "https://staging.industriasrochell.com.do");
        var mail = await PrintAsync(h, s.Dispatch, PrintDocumentTypes.DeliveryNote, delivery);
        var unknown = await Assert.ThrowsAsync<DomainException>(() => PrintAsync(h, s.Dispatch, "RECIBO", delivery));

        Assert.Equal(0, draft.GetProperty("formatVersion").GetInt32()); // E-PRT-01-4: the built-in «Rochell» format
        Assert.Contains("BORRADOR – NO DESPACHADO", draft.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("driver-qr", draft.GetProperty("body").GetString(), StringComparison.Ordinal);
        var body = screen.GetProperty("body").GetString()!;
        Assert.DoesNotContain("watermark", body, StringComparison.Ordinal);
        Assert.Contains("data-url=\"https://staging.industriasrochell.com.do/entrega/?c=", body, StringComparison.Ordinal);
        Assert.Contains("<svg", body, StringComparison.Ordinal);
        Assert.Contains("neto <span data-testid=\"conduce-net\">10,000</span> kg", body, StringComparison.Ordinal);
        Assert.Contains("Constructora Uno · RNC <span class=\"mono\">131925332</span>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("driver-qr", mail.GetProperty("body").GetString(), StringComparison.Ordinal); // the e-mail is for the customer
        Assert.Contains("@font-face", screen.GetProperty("html").GetString(), StringComparison.Ordinal); // E-PRT-01-6
        Assert.Equal(QueryErrors.InvalidParameter, unknown.Code);
    }

    [Fact]
    public async Task The_companys_active_format_replaces_the_built_in_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (order, line) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 100m);
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, s, order, line, 100m, own: false, "p");
        await h.AdminRequireAsync(
            $$$"""
            INSERT INTO md.print_format (company_id, document_type, version, status, body, css, created_by, created_at, activated_by, activated_at)
            VALUES ('{{{h.CompanyId}}}', 'DELIVERY_NOTE', 1, 'RETIRED', '<p>vieja</p>', '', '{{{h.UserId}}}', now(), '{{{h.UserId}}}', now()),
                   ('{{{h.CompanyId}}}', 'DELIVERY_NOTE', 2, 'ACTIVE', '<h1>CONDUCE {{ conduce.numero }}</h1>{% for l in lineas %}<p>{{ l.unidad }}={{ l.despachado }}</p>{% endfor %}', '.doc h1{color:red}', '{{{h.UserId}}}', now(), '{{{h.UserId}}}', now()),
                   ('{{{h.CompanyId}}}', 'DELIVERY_NOTE', 3, 'DRAFT', '<p>borrador</p>', '', '{{{h.UserId}}}', now(), NULL, NULL);
            """);

        var printed = await PrintAsync(h, s.Dispatch, PrintDocumentTypes.DeliveryNote, delivery);
        var no = await h.ScalarAsync<string>("SELECT delivery_no FROM log.delivery WHERE delivery_id = @d", ("d", delivery));

        Assert.Equal(2, printed.GetProperty("formatVersion").GetInt32());
        Assert.Equal($"<h1>CONDUCE {no}</h1><p>un=100</p>", printed.GetProperty("body").GetString());
        Assert.Contains(".doc h1{color:red}", printed.GetProperty("css").GetString(), StringComparison.Ordinal);
    }
}
