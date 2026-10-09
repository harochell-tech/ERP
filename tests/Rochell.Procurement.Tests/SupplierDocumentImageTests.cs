using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Files;
using Rochell.Procurement.SupplierDocuments;
using Rochell.Tax.Ecf;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// OCR1-04 (E-OCR-5/6/7, E-OCR1-04-1…4): a photo of an invoice read by AI (simulated) — captured with what it read marked, its lines,
/// the photo in the evidence store; the same file never read twice; the RNC or NCF typed when the AI could not read them; the month's
/// limit; the reader off or not answering.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierDocumentImageTests(PostgresFixture postgres)
{
    private sealed class MemoryStore : IEvidenceStore
    {
        public ConcurrentDictionary<string, EvidenceFile> Files { get; } = new(StringComparer.Ordinal);

        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            Files[key] = new EvidenceFile(content, contentType);
            return Task.CompletedTask;
        }

        public Task<EvidenceFile?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Files.TryGetValue(key, out var file) ? file : null);
    }

    private static byte[] Photo(string seed) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes(seed)];

    private static async Task SetLimitAsync(TestHarness h, int limit)
        => await h.AdminRequireAsync(
            $"""
            DO $$ BEGIN
              PERFORM set_config('session_replication_role', 'replica', true);
              INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value)
              SELECT company_id, policy_version_id, 'ocr_monthly_readings', to_jsonb('{limit}'::text)
              FROM acc.accounting_policy_version WHERE company_id = '{h.CompanyId}' AND policy_code = 'PURCHASING' AND status = 'ACTIVE';
            END $$
            """);

    private static async Task<JsonElement> CaptureAsync(
        ReceivedDocumentTests.Scene s, SimulatedSupplierDocumentReader? reader, IEvidenceStore? store, string key, byte[] file, string? rnc = null, string? ncf = null)
        => JsonDocument.Parse((await s.H.RunAsync(
            new CaptureSupplierDocumentFromImage(s.H.CompanyId, s.W.Clerk, key, Convert.ToBase64String(file), rnc, ncf), new CaptureSupplierDocumentFromImageHandler(reader, store)))
            .ResultPayload).RootElement;

    [Fact]
    public async Task A_photo_becomes_a_document_marked_as_read_by_AI_and_the_same_file_is_not_read_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ReceivedDocumentTests.SceneAsync(h);
        await SetLimitAsync(h, 10);
        var reader = new SimulatedSupplierDocumentReader();
        var store = new MemoryStore();

        var first = await CaptureAsync(s, reader, store, "c1", Photo("factura-1"));
        var again = await CaptureAsync(s, reader, store, "c2", Photo("factura-1"));
        var id = first.GetProperty("supplierDocumentId").GetGuid();
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSupplierDocument(h.CompanyId, s.W.Clerk, id), new GetSupplierDocumentHandler())).RootElement;
        var image = detail.GetProperty("files").EnumerateArray().Single();
        var download = JsonDocument.Parse(await h.QueryAsync(
            new GetSupplierDocumentFile(h.CompanyId, s.W.Clerk, id, image.GetProperty("fileId").GetGuid()), new GetSupplierDocumentFileHandler(store))).RootElement;

        Assert.Equal("True|False|1", $"{first.GetProperty("created").GetBoolean()}|{again.GetProperty("created").GetBoolean()}|{reader.Calls}");
        Assert.Equal(id, again.GetProperty("supplierDocumentId").GetGuid());
        Assert.Equal(
            "Agregados del Este, S.R.L.|2360.0000|360.0000|issuer_rnc,issuer_name,fiscal_number,total_amount,itbis_amount,lines|AI:Arena lavada|IMAGE:image/png",
            $"{detail.GetProperty("issuerName").GetString()}|{detail.GetProperty("totalAmount").GetString()}|{detail.GetProperty("itbisAmount").GetString()}|"
            + string.Join(',', detail.GetProperty("aiFields").EnumerateArray().Select(f => f.GetString()).OrderBy(f => Array.IndexOf(
                new[] { "issuer_rnc", "issuer_name", "buyer_rnc", "fiscal_number", "doc_date", "total_amount", "itbis_amount", "lines" }, f)))
            + $"|{detail.GetProperty("lines")[0].GetProperty("source").GetString()}:{detail.GetProperty("lines")[0].GetProperty("description").GetString()}"
            + $"|{image.GetProperty("kind").GetString()}:{image.GetProperty("contentType").GetString()}");
        Assert.DoesNotContain("LINES_DO_NOT_ADD_UP", detail.GetProperty("checks").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(Photo("factura-1"), Convert.FromBase64String(download.GetProperty("contentBase64").GetString()!));
        Assert.Equal("1|READ|simulated", await h.ScalarAsync<string>("SELECT count(*) || '|' || max(outcome) || '|' || max(model) FROM pur.supplier_document_reading"));
    }

    [Fact]
    public async Task What_the_AI_could_not_read_is_typed_without_reading_the_file_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ReceivedDocumentTests.SceneAsync(h);
        await SetLimitAsync(h, 10);
        var reader = new SimulatedSupplierDocumentReader();
        reader.Next.Enqueue(new ReaderOutcome(
            true,
            new InvoiceReading(s.Supplier, "Agregados del Este", null, null, new DateOnly(2026, 10, 5), decimal.Parse("1180.00", CultureInfo.InvariantCulture), null, []),
            "simulated", 900, 120, null));
        var store = new MemoryStore();

        var asked = await CaptureAsync(s, reader, store, "c1", Photo("borrosa"));
        var typed = await CaptureAsync(s, reader, store, "c2", Photo("borrosa"), ncf: "B0100004321");

        Assert.Equal("fiscalNumber", Assert.Single(asked.GetProperty("needsInput").EnumerateArray()).GetString());
        Assert.Equal(1, reader.Calls);
        Assert.Equal(
            "B0100004321|2026-10-05|issuer_rnc,issuer_name,doc_date,total_amount",
            await h.ScalarAsync<string>("SELECT fiscal_number || '|' || doc_date || '|' || array_to_string(ai_fields, ',') FROM pur.supplier_document WHERE supplier_document_id = @d",
                ("d", typed.GetProperty("supplierDocumentId").GetGuid())));
    }

    [Fact]
    public async Task The_months_limit_the_reader_off_or_silent_and_bad_files_are_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ReceivedDocumentTests.SceneAsync(h);
        await SetLimitAsync(h, 1);
        var reader = new SimulatedSupplierDocumentReader();
        var store = new MemoryStore();

        var off = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, null, store, "o", Photo("a")));
        var noStore = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, reader, null, "n", Photo("a")));
        var notImage = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, reader, store, "t", Encoding.ASCII.GetBytes("hola, soy un texto")));
        var longPdf = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, reader, store, "p",
            Encoding.ASCII.GetBytes("%PDF-1.7\n" + string.Concat(Enumerable.Repeat("<< /Type /Page >>\n", 4)) + "<< /Type /Pages >>")));
        reader.Next.Enqueue(new ReaderOutcome(false, null, "simulated", null, null, "HTTP 529"));
        var silent = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, reader, store, "s", Photo("a")));
        await CaptureAsync(s, reader, store, "1", Photo("a"));
        var limit = await Assert.ThrowsAsync<DomainException>(() => CaptureAsync(s, reader, store, "2", Photo("b")));

        Assert.Equal(
            $"{ProcurementErrors.SupplierDocumentReaderOff}|{ProcurementErrors.SupplierDocumentStoreMissing}|{ProcurementErrors.SupplierDocumentFileInvalid}|{ProcurementErrors.SupplierDocumentFileInvalid}|{ProcurementErrors.SupplierDocumentReadFailed}|{ProcurementErrors.SupplierDocumentReadingLimit}",
            $"{off.Code}|{noStore.Code}|{notImage.Code}|{longPdf.Code}|{silent.Code}|{limit.Code}");
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM pur.supplier_document_reading"));
    }

    [Fact]
    public void The_readers_answer_keeps_only_well_formed_values()
    {
        var reading = InvoiceReadingFormat.Parse((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            """
            {"issuer_rnc":"1-01-00001-1","ncf":"b01 0000 0012","date":"2026-10-05","total":"1,180.00","itbis":"-5","buyer_rnc":"abc",
             "lines":[{"description":"Cemento","quantity":"10","unit_price":"100","amount":"1000.00"},{"description":"sin monto"}]}
            """)!);

        Assert.Equal(
            "101000011|B0100000012|2026-10-05|1180.00||-|1:Cemento:10:100:1000.00",
            $"{reading.IssuerRnc}|{reading.FiscalNumber}|{reading.Date:yyyy-MM-dd}|{reading.Total}|{reading.Itbis}|{reading.BuyerRnc ?? "-"}|"
            + string.Join(',', reading.Lines.Select(l => $"{l.LineNo}:{l.Description}:{l.Quantity}:{l.UnitPrice}:{l.Amount}")));
    }
}
