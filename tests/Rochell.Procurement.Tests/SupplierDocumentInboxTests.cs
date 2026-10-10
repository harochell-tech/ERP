using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.SupplierDocuments;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax.Ecf;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// OCR1-03 (E-OCR-4/6, E-OCR1-01-3/5/6, E-OCR1-02-8, E-OCR1-03-1…9): the printed e-CF's QR captured, joined and checked against the XML; the
/// invoice registered from a document (linked in the same step, supplier and number its own); discarding; the inbox and the detail with
/// the checks in red and the expense suggestion.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierDocumentInboxTests(PostgresFixture postgres)
{
    private static string Qr(ReceivedDocumentTests.Scene s, string encf, string total = "5900.00", string? buyer = null, string host = "ecf.dgii.gov.do")
        => $"https://{host}/eCF/ConsultaTimbre?RncEmisor={s.Supplier}&RncComprador={buyer ?? s.Company}&ENCF={encf}&FechaEmision=09-10-2026&MontoTotal={total}"
           + "&FechaFirma=09-10-2026%2010:30:00&CodigoSeguridad=AbC123";

    private static async Task<JsonElement> CaptureAsync(ReceivedDocumentTests.Scene s, string key, string url)
        => JsonDocument.Parse((await s.H.RunAsync(new CaptureSupplierDocumentFromQr(s.H.CompanyId, s.W.Clerk, key, url), new CaptureSupplierDocumentFromQrHandler())).ResultPayload).RootElement;

    private static async Task<JsonElement> DetailAsync(ReceivedDocumentTests.Scene s, Guid id)
        => JsonDocument.Parse(await s.H.QueryAsync(new GetSupplierDocument(s.H.CompanyId, s.W.Clerk, id), new GetSupplierDocumentHandler())).RootElement;

    private static string Checks(JsonElement detail) => string.Join(',', detail.GetProperty("checks").EnumerateArray().Select(c => c.GetString()));

    [Fact]
    public void The_DGII_stamp_link_is_read_and_anything_else_is_not()
    {
        var stamp = EcfStampUrl.Parse(
            "https://ecf.dgii.gov.do/testecf/ConsultaTimbre?RncEmisor=101000011&RncComprador=131925332&ENCF=E310000000045&FechaEmision=09-10-2026&MontoTotal=5900.00&FechaFirma=09-10-2026%2022:15:00&CodigoSeguridad=AbC123")!;

        Assert.Equal(
            "101000011|131925332|E310000000045|2026-10-09|5900.00|2026-10-10T02:15:00|AbC123",
            $"{stamp.IssuerRnc}|{stamp.BuyerRnc}|{stamp.Encf}|{stamp.IssueDate:yyyy-MM-dd}|{stamp.TotalAmount}|{stamp.SignedAtUtc:yyyy-MM-ddTHH:mm:ss}|{stamp.SecurityCode}");
        Assert.Null(EcfStampUrl.Parse("https://ecf.dgii.gov.do.evil.example/ConsultaTimbre?RncEmisor=101000011&ENCF=E310000000045"));
        Assert.Null(EcfStampUrl.Parse("http://ecf.dgii.gov.do/ConsultaTimbre?RncEmisor=101000011&ENCF=E310000000045"));
        Assert.Null(EcfStampUrl.Parse("https://ecf.dgii.gov.do/ConsultaTimbre?RncEmisor=1010&ENCF=E310000000045"));
        Assert.Null(EcfStampUrl.Parse("hola"));
    }

    [Fact]
    public async Task A_QR_is_captured_once_refused_when_not_ours_and_its_total_is_checked_against_the_XML()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ReceivedDocumentTests.SceneAsync(h);

        var first = await CaptureAsync(s, "q1", Qr(s, "E310000000701", total: "5800.00"));
        var again = await CaptureAsync(s, "q2", Qr(s, "E310000000701", total: "5800.00"));
        var notOurs = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, "q3", Qr(s, "E310000000702", buyer: "101999999")));
        var invalid = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, "q4", "https://example.com/?ENCF=E310000000703"));
        var id = first.GetProperty("supplierDocumentId").GetGuid();
        var beforeXml = await DetailAsync(s, id);
        ReceivedDocumentTests.Send(s, "E310000000701"); // its XML says 5,900.00
        await ReceivedDocumentTests.ImportAsync(s, "i1");
        var afterXml = await DetailAsync(s, id);

        Assert.Equal("True|False", $"{first.GetProperty("created").GetBoolean()}|{again.GetProperty("created").GetBoolean()}");
        Assert.Equal(ProcurementErrors.SupplierDocumentNotOurs, notOurs.Code);
        Assert.Equal(ProcurementErrors.SupplierDocumentQrInvalid, invalid.Code);
        Assert.Equal("5800.0000|AbC123|2026-10-09", $"{beforeXml.GetProperty("totalAmount").GetString()}|{beforeXml.GetProperty("securityCode").GetString()}|{beforeXml.GetProperty("docDate").GetString()}");
        Assert.Equal("5900.0000|5800.0000|XML", $"{afterXml.GetProperty("totalAmount").GetString()}|{afterXml.GetProperty("qrTotalAmount").GetString()}|{afterXml.GetProperty("lines")[0].GetProperty("source").GetString()}");
        Assert.Contains("QR_TOTAL_DIFFERS", Checks(afterXml));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM pur.supplier_document"));
    }

    [Fact]
    public async Task The_invoice_registered_from_a_document_is_linked_and_must_be_its_own()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ReceivedDocumentTests.SceneAsync(h);
        ReceivedDocumentTests.Send(s, "E310000000801");
        ReceivedDocumentTests.Send(s, "E340000000802");
        await ReceivedDocumentTests.ImportAsync(s, "i1");
        var invoice = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document WHERE fiscal_number = 'E310000000801'");
        var note = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document WHERE fiscal_number = 'E340000000802'");
        var today = ExpenseInvoiceTests.Today(h);
        RegisterExpenseInvoice Register(string key, string ncf, Guid document)
            => new(h.CompanyId, s.W.Clerk, key, s.W.S.Purchasing.SupplierId, ncf, today, today.AddDays(30), s.W.Plant,
                [ExpenseInvoiceTests.Line(s.W, "Cemento gris 42.5 kg", "REPARACIONES", "ITBIS_18", 10m, 500m)], SupplierDocumentId: document);

        var mismatch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register("r0", "E310000000899", invoice), new RegisterExpenseInvoiceHandler()));
        var credit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register("r1", "E340000000802", note), new RegisterExpenseInvoiceHandler()));
        var si = (await h.RunAsync(Register("r2", "E310000000801", invoice), new RegisterExpenseInvoiceHandler())).ResultRef;
        var noteDetail = await DetailAsync(s, note);
        var linked = await DetailAsync(s, invoice);

        Assert.Equal(ProcurementErrors.SupplierDocumentMismatch, mismatch.Code);
        Assert.Equal(ProcurementErrors.SupplierDocumentNotRegistrable, credit.Code);
        Assert.Equal($"REGISTERED|{si}|DRAFT", $"{linked.GetProperty("status").GetString()}|{linked.GetProperty("supplierInvoiceId").GetGuid()}|{linked.GetProperty("supplierInvoiceStatus").GetString()}");
        Assert.Equal("False|NOTE_NOT_REGISTERED", $"{noteDetail.GetProperty("registrable").GetBoolean()}|{Checks(noteDetail).Split(',').Single(c => c == "NOTE_NOT_REGISTERED")}");
    }

    [Fact]
    public async Task The_inbox_filters_and_the_detail_flags_and_suggests()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ReceivedDocumentTests.SceneAsync(h);

        // The supplier's latest expense invoice proposes category and tax type (E-OCR1-03-3).
        await ReceivedDocumentTests.ExpenseInvoiceAsync(s, "prev", "B0100000901");
        ReceivedDocumentTests.Send(s, "E310000000901");
        ReceivedDocumentTests.Send(s, "E310000000902", issuer: "131000777"); // not a supplier, not in the registry
        await ReceivedDocumentTests.ImportAsync(s, "i1");
        var stranger = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document WHERE fiscal_number = 'E310000000902'");
        var version = await h.ScalarAsync<long>("SELECT version FROM pur.supplier_document WHERE supplier_document_id = @d", ("d", stranger));
        await h.RunAsync(new DiscardSupplierDocument(h.CompanyId, s.W.Clerk, "d", stranger, version, "No es de nosotros"), new DiscardSupplierDocumentHandler());

        // A photo read by AI whose lines do not add up (as OCR1-04 will leave it).
        await h.AdminRequireAsync(
            $"""
            DO $$
            DECLARE d uuid := gen_random_uuid();
            BEGIN
              PERFORM set_config('session_replication_role', 'replica', true);
              INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, total_amount, itbis_amount, status, ai_fields, created_by, created_at, version)
              VALUES (d, '{h.CompanyId}', '{s.Supplier}', 'B0100000903', 1180, 180, 'CAPTURED', ARRAY['total_amount', 'lines'], '{h.UserId}', now(), 1);
              INSERT INTO pur.supplier_document_line (supplier_document_id, company_id, source, line_no, description, quantity, unit_price, amount, added_at)
              VALUES (d, '{h.CompanyId}', 'AI', 1, 'Varilla', 1, 900, 900, now());
            END $$
            """);
        var photo = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document WHERE fiscal_number = 'B0100000903'");
        string Inbox(string? status, string? search = null)
            => string.Join(',', JsonDocument.Parse(h.QueryAsync(new ListSupplierDocuments(h.CompanyId, s.W.Clerk, status, search), new ListSupplierDocumentsHandler()).GetAwaiter().GetResult())
                .RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fiscalNumber").GetString()).Order());

        var received = await DetailAsync(s, await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document WHERE fiscal_number = 'E310000000901'"));
        var read = await DetailAsync(s, photo);

        Assert.Equal("B0100000903,E310000000901", Inbox("CAPTURED"));
        Assert.Equal("E310000000902", Inbox("DISCARDED"));
        Assert.Equal("E310000000902", Inbox(null, "131000777"));
        Assert.Equal("Reparaciones|ITBIS_18", $"{received.GetProperty("suggestion").GetProperty("expenseCategoryName").GetString()}|{received.GetProperty("suggestion").GetProperty("taxTypeCode").GetString()}");
        Assert.Equal("LINES_DO_NOT_ADD_UP,RNC_NOT_IN_REGISTRY", Checks(read));
        Assert.Equal("total_amount,lines", string.Join(',', read.GetProperty("aiFields").EnumerateArray().Select(f => f.GetString())));
        Assert.Equal(
            "RNC_NOT_IN_REGISTRY,SUPPLIER_NOT_IN_CORE",
            Checks(await DetailAsync(s, stranger)));
    }
}
