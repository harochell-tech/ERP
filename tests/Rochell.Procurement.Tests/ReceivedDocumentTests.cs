using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Identity;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.SupplierDocuments;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax.Ecf;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// OCR1-02 (E-OCR-2/3, E-OCR1-01-2…9, E-OCR1-02-1…10): the hourly reading of the e-CF suppliers send through Alanube (simulated) —
/// captured with their XML and lines, joined to what was captured before, linked to an invoice already registered — and the commercial
/// response to the DGII: by hand, on posting, sent to Alanube until it takes it; a rejected e-CF's invoice is not posted; a voided or
/// reversed invoice returns its document to the inbox.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReceivedDocumentTests(PostgresFixture postgres)
{
    private sealed record Scene(TestHarness H, ExpenseInvoiceTests.World W, SimulatedEcfProvider Alanube, Guid Process, string Supplier, string Company);

    private static async Task<Scene> SceneAsync(TestHarness h)
    {
        var w = await ExpenseInvoiceTests.WorldAsync(h);
        await h.AdminRequireAsync(
            $"DO $$ BEGIN PERFORM set_config('session_replication_role', 'replica', true); UPDATE md.company SET rnc = '131925332' WHERE company_id = '{h.CompanyId}'; END $$");
        var supplier = await h.ScalarAsync<string>("SELECT rnc FROM md.party WHERE party_id = @p", ("p", w.S.Purchasing.SupplierId));
        var company = await h.ScalarAsync<string>("SELECT rnc FROM md.company WHERE company_id = @c", ("c", h.CompanyId));
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        return new Scene(h, w, new SimulatedEcfProvider(), await h.Sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId), supplier!, company!);
    }

    /// <summary>An e-CF 31 of the supplier: 10 sacos de cemento at 500.00 + ITBIS 900.00 = 5,900.00, signed 10:30 in Santo Domingo.</summary>
    private static ReceivedDocument Send(Scene s, string encf, string? buyer = null, string? issuer = null, string status = ReceivedStatuses.Received, string response = ReceivedStatuses.NotDeclared)
    {
        var today = ExpenseInvoiceTests.Today(s.H);
        var xml = SimulatedEcfProvider.SampleXml(
            issuer ?? s.Supplier, "Ferretería Uno, S.R.L.", buyer ?? s.Company, encf, today, today.ToDateTime(new TimeOnly(10, 30)), [("Cemento gris 42.5 kg", "10.00", "500.00", "5000.00", true)],
            "5000.00", "900.00", "5900.00");
        return s.Alanube.AddReceived(issuer ?? s.Supplier, buyer ?? s.Company, encf, new DateTimeOffset(today.ToDateTime(new TimeOnly(14, 30)), TimeSpan.Zero), "5900.00", xml, status, response);
    }

    private static async Task<JsonElement> ImportAsync(Scene s, string key)
        => JsonDocument.Parse((await s.H.RunAsync(new ImportReceivedDocuments(s.H.CompanyId, s.Process, key), new ImportReceivedDocumentsHandler(s.Alanube))).ResultPayload).RootElement;

    private static Task<string?> DocumentAsync(TestHarness h, string encf)
        => h.ScalarAsync<string>(
            """
            SELECT d.status || '|' || coalesce(d.issuer_name, '-') || '|' || d.doc_date || '|' || d.total_amount::numeric(19,2) || '|' || d.itbis_amount::numeric(19,2) || '|' ||
                   coalesce(d.security_code, '-') || '|' || to_char(d.signature_at AT TIME ZONE 'UTC', 'HH24:MI') || '|' || (d.party_id IS NOT NULL) || '|' || coalesce(d.received_status, '-') || '|' ||
                   d.commercial_response || '|' || (d.si_id IS NOT NULL) || '|' ||
                   (SELECT string_agg(l.source || ':' || l.description || ' ' || l.quantity::numeric(18,2) || 'x' || l.unit_price::numeric(19,2) || '=' || l.amount::numeric(19,2) || ' i' ||
                                      l.billing_indicator, ',' ORDER BY l.source, l.line_no) FROM pur.supplier_document_line l WHERE l.supplier_document_id = d.supplier_document_id) || '|' ||
                   (SELECT string_agg(f.kind || ':' || f.size_bytes, ',') FROM pur.supplier_document_file f WHERE f.supplier_document_id = d.supplier_document_id)
            FROM pur.supplier_document d WHERE d.fiscal_number = @n
            """,
            ("n", encf));

    private static async Task<Guid> ExpenseInvoiceAsync(Scene s, string key, string ncf)
        => (await s.H.RunAsync(
            new RegisterExpenseInvoice(s.H.CompanyId, s.W.Clerk, key, s.W.S.Purchasing.SupplierId, ncf, ExpenseInvoiceTests.Today(s.H), ExpenseInvoiceTests.Today(s.H).AddDays(30), s.W.Plant,
                [ExpenseInvoiceTests.Line(s.W, "Cemento gris 42.5 kg", "REPARACIONES", "ITBIS_18", 10m, 500m)]),
            new RegisterExpenseInvoiceHandler())).ResultRef;

    [Fact]
    public void The_DGII_XML_is_read_with_its_lines_and_the_signature_in_UTC()
    {
        var xml = SimulatedEcfProvider.SampleXml(
            "101000011", "Ferretería Uno & Cía", "131925332", "E310000000045", new DateOnly(2026, 10, 9), new DateTime(2026, 10, 9, 22, 15, 0, DateTimeKind.Unspecified),
            [("Cemento", "10.00", "500.00", "5000.00", true), ("Flete", "1.00", "300.00", "300.00", false)], "5000.00", "900.00", "6200.00");

        var ecf = ReceivedEcfXml.Parse(Encoding.UTF8.GetBytes(xml))!;

        Assert.Equal(
            "31|E310000000045|101000011|Ferretería Uno & Cía|131925332|2026-10-09|6200.00|900.00|SIMxyz|2026-10-10T02:15:00",
            string.Join('|', ecf.EcfType, ecf.Encf, ecf.IssuerRnc, ecf.IssuerName, ecf.BuyerRnc, ecf.IssueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ecf.TotalAmount, ecf.ItbisAmount,
                ecf.SecurityCode, ecf.SignedAtUtc?.ToString("s", CultureInfo.InvariantCulture)));
        Assert.Equal("1:P1:Cemento:10.00:43:500.00:5000.00:1,2:P2:Flete:1.00:43:300.00:300.00:4", string.Join(',', ecf.Lines.Select(l => $"{l.LineNo}:{l.ItemCode}:{l.Description}:{l.Quantity}:{l.UnitCode}:{l.UnitPrice}:{l.Amount}:{l.BillingIndicator}")));
        Assert.Null(ReceivedEcfXml.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><ECF>&e;</ECF>")));
        Assert.Equal(xml, Encoding.UTF8.GetString(ReceivedEcfXml.Content(Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)))!));
    }

    [Fact]
    public async Task Received_eCF_are_captured_once_with_their_XML_and_lines_and_follow_Alanube()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SceneAsync(h);
        Send(s, "E310000000101");
        Send(s, "E310000000102", buyer: "101999999"); // E-OCR1-01-6: another company's
        Send(s, "E310000000103", status: ReceivedStatuses.NotReceived); // E-OCR1-02-2
        var answered = Send(s, "E310000000104", response: ReceivedStatuses.Accepted); // answered in Alanube's portal
        Send(s, "E340000000105", issuer: "131000777"); // a credit note of an issuer that is not a supplier (E-OCR1-01-3/4)

        var first = await ImportAsync(s, "i1");
        var second = await ImportAsync(s, "i2");

        Assert.Equal("5|4|0|1|False", $"{first.GetProperty("listed")}|{first.GetProperty("created")}|{first.GetProperty("linked")}|{first.GetProperty("refused")}|{first.GetProperty("more")}");
        Assert.Equal("5|0|0", $"{second.GetProperty("listed")}|{second.GetProperty("created")}|{second.GetProperty("refreshed")}");
        Assert.Equal(
            "CAPTURED|Ferretería Uno, S.R.L.|" + ExpenseInvoiceTests.Today(h).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "|5900.00|900.00|SIMxyz|14:30|true|RECEIVED|NOT_DECLARED|false|XML:Cemento gris 42.5 kg 10.00x500.00=5000.00 i1|XML:" + (await h.ScalarAsync<int>("SELECT size_bytes FROM pur.supplier_document_file f JOIN pur.supplier_document d USING (supplier_document_id) WHERE d.fiscal_number = 'E310000000101'")),
            await DocumentAsync(h, "E310000000101"));
        Assert.Null(await DocumentAsync(h, "E310000000102"));
        Assert.Equal("NOT_RECEIVED", (await DocumentAsync(h, "E310000000103"))!.Split('|')[8]);
        Assert.Equal("ACCEPTED|true", await h.ScalarAsync<string>("SELECT commercial_response || '|' || (response_sent_at IS NOT NULL) FROM pur.supplier_document WHERE provider_id = @p", ("p", answered.Id)));
        Assert.Equal("false", (await DocumentAsync(h, "E340000000105"))!.Split('|')[7]);
        Assert.Equal("true|null", await h.ScalarAsync<string>("SELECT (last_success_at IS NOT NULL) || '|' || coalesce(last_error, 'null') FROM pur.received_document_sync"));
        // Each reading lists 4 times; the one refused is read again (it is never kept), the others once.
        Assert.Equal(
            "COMMERCIAL_RESPONSE:0,DOWNLOAD:0,RECEIVED_GET:6,RECEIVED_LIST:8",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(o || ':' || n, ',' ORDER BY o) FROM (
                  SELECT op AS o, (SELECT count(*) FROM tax.ecf_call c WHERE c.operation = op) AS n
                  FROM unnest(ARRAY['RECEIVED_LIST', 'RECEIVED_GET', 'COMMERCIAL_RESPONSE', 'DOWNLOAD']) op) x
                """));
    }

    [Fact]
    public async Task Alanube_down_leaves_the_reading_for_the_next_pass_with_its_error()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SceneAsync(h);
        Send(s, "E310000000201");
        s.Alanube.Down = true;

        var failed = await ImportAsync(s, "i1");
        s.Alanube.Down = false;
        var recovered = await ImportAsync(s, "i2");

        Assert.StartsWith("Alanube no respondió la lista de recibidos", failed.GetProperty("error").GetString());
        Assert.Equal(1, recovered.GetProperty("created").GetInt32());
        Assert.Equal("true|null", await h.ScalarAsync<string>("SELECT (last_success_at IS NOT NULL) || '|' || coalesce(last_error, 'null') FROM pur.received_document_sync"));
    }

    [Fact]
    public async Task The_XML_joins_the_document_read_before_and_replaces_what_the_AI_read()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SceneAsync(h);

        // As OCR1-04 will leave it: a photo read by AI (total and lines wrong).
        await h.AdminRequireAsync(
            $"""
            DO $$
            DECLARE d uuid := gen_random_uuid();
            BEGIN
              PERFORM set_config('session_replication_role', 'replica', true);
              INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, total_amount, status, ai_fields, created_by, created_at, version)
              VALUES (d, '{h.CompanyId}', '{s.Supplier}', 'E310000000301', 5800, 'CAPTURED', ARRAY['total_amount', 'lines'], '{h.UserId}', now(), 1);
              INSERT INTO pur.supplier_document_line (supplier_document_id, company_id, source, line_no, description, quantity, unit_price, amount, added_at)
              VALUES (d, '{h.CompanyId}', 'AI', 1, 'Cemento gris', 10, 490, 4900, now());
            END $$
            """);
        Send(s, "E310000000301");

        var import = await ImportAsync(s, "i1");

        Assert.Equal("0|1", $"{import.GetProperty("created")}|{import.GetProperty("attached")}");
        Assert.Equal(
            "5900.00|{}|AI:Cemento gris 10.00x490.00=4900.00 i,XML:Cemento gris 42.5 kg 10.00x500.00=5000.00 i1",
            await h.ScalarAsync<string>(
                """
                SELECT d.total_amount::numeric(19,2) || '|' || d.ai_fields::text || '|' ||
                       (SELECT string_agg(l.source || ':' || l.description || ' ' || l.quantity::numeric(18,2) || 'x' || l.unit_price::numeric(19,2) || '=' || l.amount::numeric(19,2) || ' i' ||
                                          coalesce(l.billing_indicator::text, ''), ',' ORDER BY l.source) FROM pur.supplier_document_line l WHERE l.supplier_document_id = d.supplier_document_id)
                FROM pur.supplier_document d
                """));
    }

    [Fact]
    public async Task An_invoice_already_registered_is_linked_posting_accepts_and_the_answer_reaches_Alanube()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SceneAsync(h);
        var si = await ExpenseInvoiceAsync(s, "r", "E310000000401");
        var sent = Send(s, "E310000000401");

        var import = await ImportAsync(s, "i1");
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.W.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.W.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var document = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document");
        var answer = await h.ScalarAsync<string>("SELECT status || '|' || commercial_response || '|' || (responded_by = (SELECT user_id FROM iam.session WHERE session_id = @s)) || '|' || (response_sent_at IS NULL) FROM pur.supplier_document", ("s", s.W.Clerk));
        var send = JsonDocument.Parse((await h.RunAsync(new SendSupplierDocumentResponse(h.CompanyId, s.Process, "x", document), new SendSupplierDocumentResponseHandler(s.Alanube))).ResultPayload).RootElement;
        var again = JsonDocument.Parse((await h.RunAsync(new SendSupplierDocumentResponse(h.CompanyId, s.Process, "x2", document), new SendSupplierDocumentResponseHandler(s.Alanube))).ResultPayload).RootElement;

        Assert.Equal(1, import.GetProperty("linked").GetInt32());
        Assert.Equal("REGISTERED|ACCEPTED|true|true", answer);
        Assert.Equal("sent|nothing to send", $"{send.GetProperty("outcome").GetString()}|{again.GetProperty("outcome").GetString()}");
        Assert.Equal((sent.Id, true, (string?)null), Assert.Single(s.Alanube.Responses));
    }

    [Fact]
    public async Task A_rejected_eCF_is_answered_with_its_reason_and_its_invoice_is_not_posted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SceneAsync(h);
        Send(s, "E310000000501");
        await ImportAsync(s, "i1");
        var si = await ExpenseInvoiceAsync(s, "r", "E310000000501");
        await ImportAsync(s, "i2"); // links it
        var document = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document");
        var version = await h.ScalarAsync<long>("SELECT version FROM pur.supplier_document");

        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RespondToSupplierDocument(h.CompanyId, s.W.Clerk, "n", document, version, false, " "), new RespondToSupplierDocumentHandler()));
        await h.RunAsync(new RespondToSupplierDocument(h.CompanyId, s.W.Clerk, "r1", document, version, false, "No se pidió este cemento"), new RespondToSupplierDocumentHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RespondToSupplierDocument(h.CompanyId, s.W.Clerk, "r2", document, version + 1, true), new RespondToSupplierDocumentHandler()));
        s.Alanube.Down = true;
        var down = JsonDocument.Parse((await h.RunAsync(new SendSupplierDocumentResponse(h.CompanyId, s.Process, "x1", document), new SendSupplierDocumentResponseHandler(s.Alanube))).ResultPayload).RootElement;
        s.Alanube.Down = false;
        await h.RunAsync(new SendSupplierDocumentResponse(h.CompanyId, s.Process, "x2", document), new SendSupplierDocumentResponseHandler(s.Alanube));
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.W.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        var post = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.W.Clerk, "p", si, 2), new PostSupplierInvoiceHandler()));

        Assert.Equal(ProcurementErrors.ReasonRequired, noReason.Code);
        Assert.Equal(ProcurementErrors.SupplierDocumentNotAnswerable, twice.Code);
        Assert.Equal("not sent", down.GetProperty("outcome").GetString());
        Assert.Equal((false, (string?)"No se pidió este cemento"), (Assert.Single(s.Alanube.Responses).Accept, s.Alanube.Responses[0].Reason));
        Assert.Equal(ProcurementErrors.SupplierDocumentRejected, post.Code);
        Assert.Equal("REJECTED|true", await h.ScalarAsync<string>("SELECT commercial_response || '|' || (response_sent_at IS NOT NULL) FROM pur.supplier_document"));
    }

    [Fact]
    public async Task Voiding_or_reversing_the_invoice_returns_its_document_and_a_posted_invoices_eCF_is_not_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SceneAsync(h);
        Send(s, "E310000000601");
        var voided = await ExpenseInvoiceAsync(s, "r1", "E310000000601");
        await ImportAsync(s, "i1");

        await h.RunAsync(new VoidSupplierInvoice(h.CompanyId, s.W.Clerk, "v", voided, 1, "Digitada mal"), new VoidSupplierInvoiceHandler());
        var afterVoid = await h.ScalarAsync<string>("SELECT status || '|' || (si_id IS NULL) FROM pur.supplier_document");
        var si = await ExpenseInvoiceAsync(s, "r2", "E310000000601");
        await ImportAsync(s, "i2"); // the new invoice is linked
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.W.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.W.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var document = await h.ScalarAsync<Guid>("SELECT supplier_document_id FROM pur.supplier_document");
        var accepted = await h.ScalarAsync<string>("SELECT commercial_response FROM pur.supplier_document");

        // Were it not answered yet, a posted invoice's e-CF could not be rejected.
        await h.AdminRequireAsync(
            """
            DO $$ BEGIN
              PERFORM set_config('session_replication_role', 'replica', true);
              UPDATE pur.supplier_document SET commercial_response = 'NOT_DECLARED', responded_by = NULL, responded_at = NULL, version = version + 1;
            END $$
            """);
        var version = await h.ScalarAsync<long>("SELECT version FROM pur.supplier_document");
        var reject = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RespondToSupplierDocument(h.CompanyId, s.W.Clerk, "rj", document, version, false, "No era nuestro"), new RespondToSupplierDocumentHandler()));
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.W.Controller, "x", si, 3, "Registrada dos veces"), new ReverseSupplierInvoiceHandler());

        Assert.Equal("CAPTURED|true", afterVoid);
        Assert.Equal("ACCEPTED", accepted);
        Assert.Equal(ProcurementErrors.SupplierDocumentPosted, reject.Code);
        Assert.Equal(
            "CAPTURED|true|CAPTURED:3,REGISTERED:2",
            await h.ScalarAsync<string>(
                """
                SELECT d.status || '|' || (d.si_id IS NULL) || '|' ||
                       (SELECT string_agg(to_state || ':' || n, ',' ORDER BY to_state) FROM (
                          SELECT to_state, count(*) AS n FROM core.state_history WHERE aggregate_type = 'SupplierDocument' GROUP BY to_state) x)
                FROM pur.supplier_document d
                """));
    }
}
