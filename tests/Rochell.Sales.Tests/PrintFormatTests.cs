using System.Text.Json;
using Rochell.Identity.Authorization;
using Rochell.Platform.Commands;
using Rochell.Sales.Orders;
using Rochell.Sales.Printing;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// PRT-02 (E-PRT-3…10, E-PRT-02-1…7): the Director keeps a draft per document, previews it, activates it once the mandatory content
/// is there, and restores earlier versions; templates that run scripts are refused; the logo prints when the format shows it.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PrintFormatTests(PostgresFixture postgres)
{
    // A 1×1 PNG.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    private static PrintSettings Simple(string type, Func<PrintSettings, PrintSettings> change) => change(PrintFormatRules.Default(type));

    [Fact]
    public async Task The_director_hides_a_column_adds_a_footer_previews_and_activates_and_the_conduce_prints_with_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (order, line) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 100m);
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, s, order, line, 100m, own: false, "p");
        var director = await h.SessionWithRolesAsync("DIRECTOR");
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var settings = Simple(PrintDocumentTypes.DeliveryNote, x => x with
        {
            Paper = PrintFormatRules.HalfLetter,
            Columns = [.. x.Columns.Select(c => c.Key == "lotes" ? c with { Show = false } : c.Key == "producto" ? c with { Title = "Artículo", WidthPercent = 40 } : c)],
            Texts = x.Texts with { Footer = "Gracias por preferirnos." },
        });

        var saved = JsonDocument.Parse((await h.RunAsync(new SavePrintFormatDraft(h.CompanyId, director, "d1", PrintDocumentTypes.DeliveryNote, settings, Note: "Media carta"),
            new SavePrintFormatDraftHandler())).ResultPayload).RootElement;
        var again = JsonDocument.Parse((await h.RunAsync(new SavePrintFormatDraft(h.CompanyId, director, "d2", PrintDocumentTypes.DeliveryNote, settings),
            new SavePrintFormatDraftHandler())).ResultPayload).RootElement;
        var preview = JsonDocument.Parse(await h.QueryAsync(new PreviewPrintFormat(h.CompanyId, contador, PrintDocumentTypes.DeliveryNote, settings), new PreviewPrintFormatHandler()))
            .RootElement;
        await h.RunAsync(new ActivatePrintFormat(h.CompanyId, director, "a1", PrintDocumentTypes.DeliveryNote, 1), new ActivatePrintFormatHandler());
        var printed = JsonDocument.Parse(await h.QueryAsync(new GetPrintDocument(h.CompanyId, s.Dispatch, PrintDocumentTypes.DeliveryNote, delivery), new GetPrintDocumentHandler()))
            .RootElement;
        var list = JsonDocument.Parse(await h.QueryAsync(new ListPrintFormats(h.CompanyId, contador), new ListPrintFormatsHandler())).RootElement.GetProperty("types");
        var seller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SavePrintFormatDraft(h.CompanyId, s.Seller, "x", PrintDocumentTypes.DeliveryNote, settings),
            new SavePrintFormatDraftHandler()));

        Assert.Equal((1, 1), (saved.GetProperty("version").GetInt32(), again.GetProperty("version").GetInt32())); // one draft per type
        var previewBody = preview.GetProperty("document").GetProperty("body").GetString()!;
        Assert.Contains("VISTA PREVIA", previewBody, StringComparison.Ordinal);
        Assert.Contains(">Artículo</th>", previewBody, StringComparison.Ordinal);
        Assert.DoesNotContain(">Lotes</th>", previewBody, StringComparison.Ordinal);
        Assert.False(preview.GetProperty("example").GetBoolean()); // the latest real conduce
        Assert.Equal(1, printed.GetProperty("formatVersion").GetInt32());
        Assert.Contains("Gracias por preferirnos.", printed.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains("@page{size:5.5in 8.5in;margin:12mm}", printed.GetProperty("css").GetString(), StringComparison.Ordinal);
        Assert.Equal((1, JsonValueKind.Null), (list[0].GetProperty("activeVersion").GetInt32(), list[0].GetProperty("draftVersion").ValueKind));
        Assert.Equal(AuthorizationErrors.NotAuthorized, seller.Code);
    }

    [Fact]
    public async Task Unsafe_or_broken_templates_are_refused_and_an_invoice_without_its_fiscal_data_is_not_activated()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var director = await h.SessionWithRolesAsync("DIRECTOR");
        var advanced = PrintFormatRules.Default(PrintDocumentTypes.Invoice) with { Mode = PrintFormatRules.Advanced };

        var script = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SavePrintFormatDraft(h.CompanyId, director, "s", PrintDocumentTypes.Invoice, advanced, "<p onclick=\"x()\">{{ factura.numero }}</p>", string.Empty), new SavePrintFormatDraftHandler()));
        var external = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SavePrintFormatDraft(h.CompanyId, director, "e", PrintDocumentTypes.Invoice, advanced, "<p>{{ factura.numero }}</p>", "@import url(https://x/y.css);"), new SavePrintFormatDraftHandler()));
        var broken = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SavePrintFormatDraft(h.CompanyId, director, "b", PrintDocumentTypes.Invoice, advanced, "{% for l in %}", string.Empty), new SavePrintFormatDraftHandler()));
        var ticket = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SavePrintFormatDraft(h.CompanyId, director, "t", PrintDocumentTypes.Quote, PrintFormatRules.Default(PrintDocumentTypes.Quote) with { Paper = PrintFormatRules.Ticket80 }),
            new SavePrintFormatDraftHandler()));
        await h.RunAsync(new SavePrintFormatDraft(h.CompanyId, director, "ok", PrintDocumentTypes.Invoice, advanced, "<h1>Factura {{ factura.numero }}</h1><p>{{ emisor.rnc }}</p>", string.Empty),
            new SavePrintFormatDraftHandler());
        var mandatory = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivatePrintFormat(h.CompanyId, director, "a", PrintDocumentTypes.Invoice, 1), new ActivatePrintFormatHandler()));
        var restored = JsonDocument.Parse((await h.RunAsync(new RestorePrintFormat(h.CompanyId, director, "r", PrintDocumentTypes.Invoice, 0), new RestorePrintFormatHandler())).ResultPayload).RootElement;
        await h.RunAsync(new ActivatePrintFormat(h.CompanyId, director, "a2", PrintDocumentTypes.Invoice, 1), new ActivatePrintFormatHandler());

        Assert.Equal((PrintErrors.TemplateUnsafe, PrintErrors.TemplateUnsafe, PrintErrors.TemplateInvalid, PrintErrors.SettingsInvalid), (script.Code, external.Code, broken.Code, ticket.Code));
        Assert.Equal(PrintErrors.MandatoryMissing, mandatory.Code);
        Assert.Contains("el e-NCF", mandatory.Message, StringComparison.Ordinal);
        Assert.Contains("el código QR del e-CF", mandatory.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RNC del emisor", mandatory.Message, StringComparison.Ordinal);
        Assert.Equal(1, restored.GetProperty("version").GetInt32()); // the same draft, now the built-in format
        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status FROM md.print_format WHERE document_type = 'INVOICE' AND version = 1"));
    }

    [Fact]
    public async Task The_logo_prints_where_the_format_shows_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var director = await h.SessionWithRolesAsync("DIRECTOR");

        var svg = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SetCompanyLogo(h.CompanyId, director, "svg", Convert.ToBase64String("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray())), new SetCompanyLogoHandler()));
        await h.RunAsync(new SetCompanyLogo(h.CompanyId, director, "png", Png), new SetCompanyLogoHandler());
        var preview = JsonDocument.Parse(await h.QueryAsync(
            new PreviewPrintFormat(h.CompanyId, director, PrintDocumentTypes.Quote, PrintFormatRules.Default(PrintDocumentTypes.Quote) with { ShowLogo = true }), new PreviewPrintFormatHandler()))
            .RootElement;

        Assert.Equal(PrintErrors.LogoInvalid, svg.Code);
        Assert.True(preview.GetProperty("example").GetBoolean()); // no quote yet: the example
        Assert.Contains("<img class=\"logo\" src=\"data:image/png;base64,", preview.GetProperty("document").GetProperty("body").GetString(), StringComparison.Ordinal);
    }
}
