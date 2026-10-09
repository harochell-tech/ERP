using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// OCR1-01 (E-OCR-1…8, E-OCR1-01-1…10): what the database guarantees about captured supplier documents — one live document per issuer
/// and fiscal number, the way it goes to an invoice and back, the commercial response given once, insert-only lines and files, and
/// the seeds. No command exists yet, so each case is a block that sets its rows up, runs the statement under test and rolls everything
/// back with a sentinel.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierDocumentSchemaTests(PostgresFixture postgres)
{
    private const string Done = "P0099";

    private sealed record World(TestHarness H, string Setup);

    /// <summary>A captured e-CF «doc» of RNC 101000011 still waiting, Alanube's id known, and a DRAFT expense invoice «si».</summary>
    private static async Task<World> WorldAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var setup = $"""
            DECLARE
              c uuid := '{h.CompanyId}'; u uuid := '{h.UserId}'; plant uuid := '{p.PlantId}'; supplier uuid := '{p.SupplierId}';
              doc uuid := gen_random_uuid(); si uuid := gen_random_uuid();
            BEGIN
              INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version,
                                                doc_class, plant_id)
              VALUES (si, c, supplier, 'E310000000123', DATE '2026-10-02', DATE '2026-11-01', 'DRAFT', 'NOT_POSTED', 1180, u, 1, 'EXPENSE', plant);
              INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, issuer_name, buyer_rnc, fiscal_number, doc_date, total_amount, itbis_amount,
                                                 provider_id, received_status, status, created_by, created_at, version)
              VALUES (doc, c, '101000011', 'Ferretería Uno', '131925332', 'E310000000123', DATE '2026-10-02', 1180, 180, 'alanube-1', 'RECEIVED', 'CAPTURED', u, now(), 1);
            """;
        return new World(h, setup);
    }

    private static async Task<string?> ProbeAsync(World w, string statements)
        => (await w.H.AdminExecuteAsync($"DO $$ {w.Setup} {statements} RAISE EXCEPTION 'done' USING ERRCODE = '{Done}'; END $$"))?.SqlState;

    private const string Line = "INSERT INTO pur.supplier_document_line (supplier_document_id, company_id, source, line_no, description, quantity, unit_price, itbis_amount, amount, added_at) VALUES ";

    [Fact]
    public async Task A_document_takes_lines_and_files_goes_to_its_invoice_and_back_and_is_answered_once()
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(Done, await ProbeAsync(w, Line + """
                (doc, c, 'AI', 1, 'Cemento gris', 10, 100, 180, 1000, now()),
                (doc, c, 'XML', 1, 'Cemento gris Portland', 10, 100, 180, 1000, now());
                INSERT INTO pur.supplier_document_file VALUES
                  (gen_random_uuid(), c, doc, 'XML', 'application/xml', '\x3c3f786d6c3f3e', NULL, 7, sha256('\x3c3f786d6c3f3e'), u, now()),
                  (gen_random_uuid(), c, doc, 'IMAGE', 'image/jpeg', NULL, 'supplier-documents/a.jpg', 1000, sha256('\x00'), u, now());
                UPDATE pur.supplier_document SET party_id = supplier, ai_fields = ARRAY['lines'], version = 2 WHERE supplier_document_id = doc;
                UPDATE pur.supplier_document SET status = 'REGISTERED', si_id = si, version = 3 WHERE supplier_document_id = doc;
                UPDATE pur.supplier_document SET status = 'CAPTURED', si_id = NULL, version = 4 WHERE supplier_document_id = doc;
                UPDATE pur.supplier_document SET commercial_response = 'ACCEPTED', responded_by = u, responded_at = now(), version = 5 WHERE supplier_document_id = doc;
                UPDATE pur.supplier_document SET response_sent_at = now(), version = 6 WHERE supplier_document_id = doc;
                UPDATE pur.supplier_document SET status = 'DISCARDED', discard_reason = 'Duplicado', version = 7 WHERE supplier_document_id = doc;
                INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, created_by, created_at, version)
                VALUES (gen_random_uuid(), c, '101000011', 'E310000000123', 'CAPTURED', u, now(), 1);
                """));
        }
    }

    [Theory]
    // E-OCR1-01-5: one live document per issuer and fiscal number; Alanube's id once.
    [InlineData("INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, created_by, created_at, version) VALUES (gen_random_uuid(), c, '101000011', 'E310000000123', 'CAPTURED', u, now(), 1);", SqlStates.UniqueViolation)]
    [InlineData("INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, provider_id, status, created_by, created_at, version) VALUES (gen_random_uuid(), c, '101000012', 'E310000000124', 'alanube-1', 'CAPTURED', u, now(), 1);", SqlStates.UniqueViolation)]
    // Formats: RNC 9 or cédula 11 digits; NCF or e-NCF as on supplier invoices.
    [InlineData("INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, created_by, created_at, version) VALUES (gen_random_uuid(), c, '1010', 'E310000000125', 'CAPTURED', u, now(), 1);", SqlStates.CheckViolation)]
    [InlineData("INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, created_by, created_at, version) VALUES (gen_random_uuid(), c, '101000011', 'A0100000001', 'CAPTURED', u, now(), 1);", SqlStates.CheckViolation)]
    // Born CAPTURED, version 1.
    [InlineData("INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, si_id, created_by, created_at, version) VALUES (gen_random_uuid(), c, '101000011', 'B0100000001', 'REGISTERED', si, u, now(), 1);", SqlStates.RaiseException)]
    // The issuer and the fiscal number never change; it is never deleted.
    [InlineData("UPDATE pur.supplier_document SET fiscal_number = 'E310000000999', version = 2 WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.supplier_document SET provider_id = 'alanube-2', version = 2 WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    [InlineData("DELETE FROM pur.supplier_document WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    // REGISTERED ⇔ an invoice; DISCARDED ⇔ a reason; DISCARDED is final; nothing of the header moves once registered.
    [InlineData("UPDATE pur.supplier_document SET status = 'REGISTERED', version = 2 WHERE supplier_document_id = doc;", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.supplier_document SET status = 'DISCARDED', version = 2 WHERE supplier_document_id = doc;", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.supplier_document SET status = 'DISCARDED', discard_reason = 'Duplicado', version = 2 WHERE supplier_document_id = doc; UPDATE pur.supplier_document SET status = 'CAPTURED', discard_reason = NULL, version = 3 WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.supplier_document SET status = 'REGISTERED', si_id = si, version = 2 WHERE supplier_document_id = doc; UPDATE pur.supplier_document SET total_amount = 1, version = 3 WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.supplier_document SET total_amount = 1, version = 3 WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    // E-OCR1-01-1: an invoice comes from one document.
    [InlineData("UPDATE pur.supplier_document SET status = 'REGISTERED', si_id = si, version = 2 WHERE supplier_document_id = doc; INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, created_by, created_at, version) VALUES (gen_random_uuid(), c, '101000011', 'B0100000001', 'CAPTURED', u, now(), 1);", null)]
    // E-OCR-3: the commercial response once; a rejection with its reason; only an e-CF that reached Alanube.
    [InlineData("UPDATE pur.supplier_document SET commercial_response = 'REJECTED', responded_by = u, responded_at = now(), version = 2 WHERE supplier_document_id = doc;", SqlStates.CheckViolation)]
    [InlineData("UPDATE pur.supplier_document SET commercial_response = 'ACCEPTED', responded_by = u, responded_at = now(), version = 2 WHERE supplier_document_id = doc; UPDATE pur.supplier_document SET commercial_response = 'REJECTED', response_reason = 'No pedido', version = 3 WHERE supplier_document_id = doc;", SqlStates.RaiseException)]
    [InlineData("UPDATE pur.supplier_document SET response_sent_at = now(), version = 2 WHERE supplier_document_id = doc;", SqlStates.CheckViolation)]
    [InlineData("INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, fiscal_number, status, commercial_response, responded_by, responded_at, created_by, created_at, version) VALUES (gen_random_uuid(), c, '101000011', 'E310000000777', 'CAPTURED', 'ACCEPTED', u, now(), u, now(), 1);", SqlStates.CheckViolation)]
    // What the AI read is named from a fixed list.
    [InlineData("UPDATE pur.supplier_document SET ai_fields = ARRAY['precio'], version = 2 WHERE supplier_document_id = doc;", SqlStates.CheckViolation)]
    public async Task Documents_are_guarded(string statement, string? expected)
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(expected ?? Done, await ProbeAsync(w, statement));
        }
    }

    [Theory]
    [InlineData("(doc, c, 'XML', 1, 'Cemento', 0, 100, NULL, 0, now());", SqlStates.CheckViolation)]
    [InlineData("(doc, c, 'OCR', 1, 'Cemento', 1, 100, NULL, 100, now());", SqlStates.CheckViolation)]
    [InlineData("(doc, c, 'XML', 1, ' ', 1, 100, NULL, 100, now());", SqlStates.CheckViolation)]
    [InlineData("(doc, c, 'XML', 1, 'Cemento', 1, 100, NULL, 100, now()); UPDATE pur.supplier_document_line SET amount = 1;", SqlStates.RaiseException)]
    public async Task Lines_are_read_once_per_source(string values, string expected)
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(expected, await ProbeAsync(w, Line + values));
        }
    }

    [Theory]
    // E-OCR1-01-7: the XML in the database (one), a picture or PDF in the evidence store, ≤ 10 MB, of an allowed type.
    [InlineData("(gen_random_uuid(), c, doc, 'XML', 'application/xml', NULL, 'k', 7, sha256('\\x00'), u, now())", SqlStates.CheckViolation)]
    [InlineData("(gen_random_uuid(), c, doc, 'IMAGE', 'image/jpeg', '\\x00', NULL, 1, sha256('\\x00'), u, now())", SqlStates.CheckViolation)]
    [InlineData("(gen_random_uuid(), c, doc, 'IMAGE', 'image/gif', NULL, 'k', 1, sha256('\\x00'), u, now())", SqlStates.CheckViolation)]
    [InlineData("(gen_random_uuid(), c, doc, 'PDF', 'application/pdf', NULL, 'k', 10485761, sha256('\\x00'), u, now())", SqlStates.CheckViolation)]
    [InlineData("(gen_random_uuid(), c, doc, 'XML', 'application/xml', '\\x00', NULL, 1, sha256('\\x00'), u, now()), (gen_random_uuid(), c, doc, 'XML', 'text/xml', '\\x01', NULL, 1, sha256('\\x01'), u, now())", SqlStates.UniqueViolation)]
    public async Task Files_are_kept_where_they_belong(string values, string expected)
    {
        var w = await WorldAsync(postgres);
        await using (w.H)
        {
            Assert.Equal(expected, await ProbeAsync(w, $"INSERT INTO pur.supplier_document_file VALUES {values};"));
        }
    }

    [Theory]
    [InlineData("UPDATE pur.supplier_document SET fiscal_number = 'B0100000001'")]
    [InlineData("UPDATE pur.supplier_document SET created_by = NULL")]
    [InlineData("DELETE FROM pur.supplier_document")]
    [InlineData("UPDATE pur.supplier_document_line SET amount = 1")]
    [InlineData("UPDATE pur.supplier_document_file SET storage_key = 'x'")]
    [InlineData("DELETE FROM pur.received_document_sync")]
    public async Task The_application_never_rewrites_what_was_read(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState); // insufficient_privilege
    }

    [Fact]
    public async Task Reception_calls_are_recorded_and_the_permissions_are_seeded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var operations = await h.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ecf_call_operation'");
        Assert.All(new[] { "RECEIVED_LIST", "RECEIVED_GET", "COMMERCIAL_RESPONSE", "SUBMIT" }, o => Assert.Contains(o, operations));
        Assert.Equal("CONTADOR:supplier_document:respond,CUENTAS_POR_PAGAR:supplier_document:capture,CUENTAS_POR_PAGAR:supplier_document:respond,SUPERADMIN:supplier_document:capture,SUPERADMIN:supplier_document:respond",
            await h.ScalarAsync<string>("""
                SELECT string_agg(r.code || ':' || rp.permission_code, ',' ORDER BY r.code, rp.permission_code)
                FROM iam.role_permission rp JOIN iam.role r USING (role_id) WHERE rp.permission_code LIKE 'supplier_document:%'
                """));
    }
}
