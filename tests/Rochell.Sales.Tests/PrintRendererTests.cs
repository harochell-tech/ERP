using Rochell.Platform.Commands;
using Rochell.Sales.Printing;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>PRT-01 (E-PRT-01-1…3, 6): the Liquid renderer and the server's paper formats.</summary>
public sealed class PrintRendererTests
{
    [Fact]
    public void A_template_reads_only_its_model_and_its_output_is_encoded()
    {
        var model = new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?> { ["name"] = "Ferretería <Uno> & Co." },
            ["lines"] = new List<object?> { new Dictionary<string, object?> { ["code"] = "BLOQUE-6", ["qty"] = "1,000" }, new Dictionary<string, object?> { ["code"] = "BLOQUE-8", ["qty"] = "40" } },
            ["voided"] = false,
        };

        var doc = PrintRenderer.Render(
            new PrintTemplate("<p>{{ customer.name }}</p>{% for l in lines %}<i>{{ l.code }}:{{ l.qty }}</i>{% endfor %}{% if voided %}ANULADA{% endif %}", ".x{}"), "Prueba", model);

        Assert.Equal("<p>Ferretería &lt;Uno&gt; &amp; Co.</p><i>BLOQUE-6:1,000</i><i>BLOQUE-8:40</i>", doc.Body);
        Assert.Contains("@font-face{font-family:\"IBM Plex Sans\";font-weight:400", doc.Html, StringComparison.Ordinal);
        Assert.Contains(".x{}", doc.Css, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html>", doc.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_that_does_not_parse_says_so()
    {
        var bad = Assert.Throws<DomainException>(() => PrintRenderer.Render(new PrintTemplate("{% for x in %}", string.Empty), "x", new Dictionary<string, object?>()));
        Assert.Equal(PrintErrors.TemplateInvalid, bad.Code);
    }

    [Theory]
    [InlineData("5900.0000", "5,900.00", "5,900")]
    [InlineData("12.3450", "12.345", "12.345")]
    [InlineData("-0.004", "-0.004", "-0.004")]
    [InlineData("1234567.5", "1,234,567.50", "1,234,567.5")]
    [InlineData("0", "0.00", "0")]
    public void Amounts_and_quantities_print_as_on_screen(string value, string money, string quantity)
    {
        var d = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((money, quantity), (PrintText.Money(d), PrintText.Quantity(d)));
    }

    [Fact]
    public void Dates_print_in_dominican_time()
    {
        Assert.Equal("08/10/2026", PrintText.Date(new DateOnly(2026, 10, 8)));
        Assert.Equal("29/09/2026 11:15 p. m.", PrintText.DateTime(new DateTime(2026, 9, 30, 3, 15, 0, DateTimeKind.Utc)));
        Assert.Equal("30/09/2026 12:05 a. m.", PrintText.DateTime(new DateTime(2026, 9, 30, 4, 5, 0, DateTimeKind.Utc)));
    }
}
