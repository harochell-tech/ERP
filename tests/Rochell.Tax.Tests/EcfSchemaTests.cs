using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>
/// VS4-01 (E-VS4-01-1…10): the e-CF gateway's schema — what the application may and may not do to ranges, documents, calls and files, the
/// DGII unit codes, and the invoice / credit note fiscal statuses of the gateway.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EcfSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Units_carry_their_DGII_code()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal("kg:21,l:24,m3:28,t:39,un:43", await h.ScalarAsync<string>("SELECT string_agg(uom_code || ':' || dgii_code, ',' ORDER BY uom_code) FROM md.uom"));
    }

    [Theory]
    [InlineData("DELETE FROM tax.ecf_series")]
    [InlineData("UPDATE tax.ecf_series SET ecf_type = '31'")]
    [InlineData("UPDATE tax.ecf_document SET encf = 'E310000000001'")]
    [InlineData("UPDATE tax.ecf_document SET payload = '{}'")]
    [InlineData("DELETE FROM tax.ecf_document")]
    [InlineData("UPDATE tax.ecf_call SET outcome = 'OK'")]
    [InlineData("UPDATE tax.ecf_file SET content = '\\x00'")]
    [InlineData("UPDATE md.uom SET dgii_code = 1")]
    public async Task The_application_never_rewrites_what_was_numbered_sent_or_received(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState); // insufficient_privilege
    }

    [Fact]
    public async Task Invoices_and_credit_notes_accept_the_gateway_statuses_and_keep_the_eNCF_rule()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var invoice = await h.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'invoice_statuses'");
        var credit = await h.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'credit_note_statuses'");
        var encf = await h.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'invoice_encf_present'");

        Assert.All(new[] { "ECF_SENDING", "ECF_ACCEPTED", "ECF_REJECTED", "ECF_ACTION" }, s => Assert.Contains(s, invoice));
        Assert.All(new[] { "ECF_SENDING", "ECF_ACCEPTED", "ECF_REJECTED", "ECF_ACTION" }, s => Assert.Contains(s, credit));
        Assert.Contains("ECF_ACCEPTED", encf);
    }
}
