using Npgsql;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// FIS1-01 schema guarantees (Frozen Baseline FIS-1 §2, §4; E-FIS1-01-1…10), written as the application role through a fixture command
/// (the commands arrive in FIS1-02 and FIS1-03).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalAuthorizationSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static async Task<(Guid Customer, Guid Supplier, Guid Block, Guid Sand)> SetupAsync(TestHarness h)
    {
        var customer = await h.CreateActiveSupplierAsync("131925332", "Hotel del Este");
        await Run(h, "customer",
            $"UPDATE md.party SET is_customer = true, customer_status = 'DRAFT', version = 2 WHERE party_id = '{customer}'",
            new TestState("Customer", customer, null, "DRAFT"));
        var supplier = await h.CreateActiveSupplierAsync("101000001", "Solo proveedor");
        var block = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        return (customer, supplier, block, await h.CreateActiveItemAsync("ARENA", "t", "AGREGADO"));
    }

    private static string Authorization(TestHarness h, Guid id, Guid party, string certificate) =>
        $"INSERT INTO tax.fiscal_authorization VALUES ('{id}', '{h.CompanyId}', '{party}', 'CONFOTUR', '{certificate}', current_date, current_date + 180, 'Hotel Punta Cana', 'CONFOTUR-123-2025', NULL, NULL, 'DRAFT', @user, NULL, 1)";

    [Fact]
    public async Task An_authorization_is_of_a_customer_verified_by_another_person_and_its_scope_changes_only_in_draft()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (customer, supplier, block, sand) = await SetupAsync(h);
        var verifier = await h.CreateUserAsync();
        var auth = Guid.CreateVersion7();
        string Line(int no, Guid item, string qty = "1000", string net = "50000.00") =>
            $"INSERT INTO tax.fiscal_authorization_line VALUES ('{auth}', {no}, '{h.CompanyId}', '{item}', 'un', {qty}, {net}, 0, 0)";

        var notCustomer = await Fails(h, "supplier", Authorization(h, Guid.CreateVersion7(), supplier, "CERT-0"), new TestState("FiscalAuthorization", Guid.Empty, null, "DRAFT"));
        await Run(h, "auth", Authorization(h, auth, customer, "CERT-1") + "; " + Line(1, block), new TestState("FiscalAuthorization", auth, null, "DRAFT"));
        var duplicate = await Fails(h, "dup", Authorization(h, Guid.CreateVersion7(), customer, "CERT-1"), new TestState("FiscalAuthorization", Guid.Empty, null, "DRAFT"));
        var rawMaterial = await Fails(h, "raw", Line(2, sand));
        var noNet = await Fails(h, "net", Line(2, block, "10", "0"));
        await Run(h, "submit", $"UPDATE tax.fiscal_authorization SET status = 'PENDING_VERIFICATION', version = 2 WHERE authorization_id = '{auth}'",
            new TestState("FiscalAuthorization", auth, "DRAFT", "PENDING_VERIFICATION"));
        var self = await Fails(h, "self", $"UPDATE tax.fiscal_authorization SET status = 'ACTIVE', verified_by = @user, version = 3 WHERE authorization_id = '{auth}'",
            new TestState("FiscalAuthorization", auth, "PENDING_VERIFICATION", "ACTIVE"));
        await Run(h, "verify", $"UPDATE tax.fiscal_authorization SET status = 'ACTIVE', verified_by = '{verifier}', version = 3 WHERE authorization_id = '{auth}'",
            new TestState("FiscalAuthorization", auth, "PENDING_VERIFICATION", "ACTIVE"));
        var lateLine = await Fails(h, "late", Line(3, block, "5", "250.00").Replace("'un'", "'m3'", StringComparison.Ordinal));
        var changeCertificate = await Fails(h, "cert", $"UPDATE tax.fiscal_authorization SET certificate_no = 'OTRO', version = 4 WHERE authorization_id = '{auth}'");
        var toDraft = await Fails(h, "draft", $"UPDATE tax.fiscal_authorization SET status = 'DRAFT', version = 4 WHERE authorization_id = '{auth}'",
            new TestState("FiscalAuthorization", auth, "ACTIVE", "DRAFT"));
        var overConsumed = await Fails(h, "over", $"UPDATE tax.fiscal_authorization_line SET qty_consumed = 1001, net_consumed = 100 WHERE authorization_id = '{auth}'");
        await Run(h, "consume", $"UPDATE tax.fiscal_authorization_line SET qty_consumed = 600, net_consumed = 30000.00 WHERE authorization_id = '{auth}'");

        Assert.Equal(("P0001", "23505", "P0001", "23514", "23514"), (notCustomer, duplicate, rawMaterial, noNet, self));
        Assert.Equal(("P0001", "P0001", "P0001", "23514"), (lateLine, changeCertificate, toDraft, overConsumed));
        Assert.Equal("ACTIVE:600.000000:30000.0000", await h.ScalarAsync<string>(
            $"SELECT a.status || ':' || l.qty_consumed || ':' || l.net_consumed FROM tax.fiscal_authorization a JOIN tax.fiscal_authorization_line l USING (authorization_id) WHERE a.authorization_id = '{auth}'"));
    }

    [Fact]
    public async Task Documents_carry_their_hash_and_are_never_changed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (customer, _, _, _) = await SetupAsync(h);
        var auth = Guid.CreateVersion7();
        await Run(h, "auth", Authorization(h, auth, customer, "CERT-1"), new TestState("FiscalAuthorization", auth, null, "DRAFT"));
        var document = Guid.CreateVersion7();
        string Document(Guid id, string kind, string sha) =>
            $"INSERT INTO tax.fiscal_authorization_document VALUES ('{id}', '{h.CompanyId}', '{auth}', '{kind}', 'certificado.pdf', '{sha}', @user, now())";

        await Run(h, "doc", Document(document, "CERTIFICADO_DGII", new string('a', 64)));
        var badHash = await Fails(h, "hash", Document(Guid.CreateVersion7(), "CERTIFICADO_DGII", "no-es-un-hash"));
        var badKind = await Fails(h, "kind", Document(Guid.CreateVersion7(), "CARTA", new string('b', 64)));
        var delete = await Fails(h, "delete", $"DELETE FROM tax.fiscal_authorization_document WHERE document_id = '{document}'");

        Assert.Equal(("23514", "23514", "42501"), (badHash, badKind, delete));
    }
}
