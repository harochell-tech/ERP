using System.Security.Cryptography;
using System.Text.Json;
using Rochell.Audit.Worm;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Audit.Tests;

/// <summary>UX4-01 · E-UX4-15: journals by document number or id, and the last verification of the hash chains.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4AuditTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _wormRoot = Path.Combine(Path.GetTempPath(), "rochell-worm-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_wormRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_wormRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_wormRoot, recursive: true);
        }
    }

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    [Fact]
    public async Task Journals_are_found_by_document_number_or_id()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync(); // RM-YYYY-000001: 6 t of sand (R-01)
        await h.EnableInvoicePostingAsync();
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var auditor = await h.SessionWithRolesAsync("AUDITOR");
        var grNo = await h.ScalarAsync<string>("SELECT gr_no FROM pur.goods_receipt");
        var grEvent = await h.ScalarAsync<Guid>("SELECT posting_event_id FROM pur.goods_receipt");
        async Task<JsonElement> Search(string text) => JsonDocument.Parse(await h.QueryAsync(new SearchJournals(h.CompanyId, auditor, text), new SearchJournalsHandler())).RootElement.GetProperty("items");
        static string Hits(JsonElement items) => string.Join(',', items.EnumerateArray().Select(i =>
            $"{i.GetProperty("ruleCode").GetString()}:{i.GetProperty("documentNumber").GetString()}:{i.GetProperty("totalDebit").GetString()}"));

        var byNumber = await Search(grNo!.ToLowerInvariant());
        var byNcf = await Search("B0100000001");
        var byDocumentId = await Search(s.GoodsReceiptId.ToString());
        var byEventId = await Search(grEvent.ToString());
        var nothing = await Search("RM-1999-000001");
        var blank = await Assert.ThrowsAsync<DomainException>(() => Search("  "));

        // R-01: Dr raw material 9 000.00 / Cr GRNI 9 000.00. R-04: Dr GRNI 9 000.00 + ITBIS 1 620.00 / Cr AP 10 620.00.
        Assert.Equal($"R-01:{grNo}:9000.00", Hits(byNumber));
        Assert.Equal("R-04:B0100000001:10620.00", Hits(byNcf));
        Assert.Equal((Hits(byNumber), Hits(byNumber)), (Hits(byDocumentId), Hits(byEventId)));
        Assert.Equal(0, nothing.GetArrayLength());
        Assert.Equal(QueryErrors.InvalidParameter, blank.Code);
    }

    [Fact]
    public async Task The_integrity_status_shows_the_last_verification_and_what_waits_to_be_sealed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateInvoicingSetupAsync();
        var auditor = await h.SessionWithRolesAsync("AUDITOR");
        async Task<JsonElement> Status() => JsonDocument.Parse(await h.QueryAsync(new GetIntegrityStatus(h.CompanyId, auditor), new GetIntegrityStatusHandler())).RootElement;
        static string Chains(JsonElement status) => string.Join(',', status.GetProperty("chains").EnumerateArray().Select(c =>
            $"{c.GetProperty("ledger").GetString()}:{(c.GetProperty("pendingSeal").GetInt32() > 0 ? "pending" : "sealed")}:{c.GetProperty("sealErrors").GetInt32()}"));

        var before = await Status();
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await h.RunAsync(new VerifyHashChain(h.CompanyId, controller, "verify"), new VerifyHashChainHandler(FileSystemWormStore.Create(_wormRoot, "TEST"), new DigestSigner(_key)));
        var after = await Status();

        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastVerification").ValueKind);
        Assert.Equal("GL:pending:0,INV_QTY:pending:0,INV_VALUE:pending:0,DOMAIN_EVENT:pending:0", Chains(before));
        var verification = after.GetProperty("lastVerification");
        Assert.Equal((true, 4), (verification.GetProperty("valid").GetBoolean(), verification.GetProperty("chains").GetArrayLength()));
        Assert.False(string.IsNullOrEmpty(verification.GetProperty("verifiedBy").GetString()));
        Assert.Equal("GL:sealed:0,INV_QTY:sealed:0,INV_VALUE:sealed:0,DOMAIN_EVENT:sealed:0", Chains(after));
        Assert.True(after.GetProperty("chains")[0].GetProperty("lastSequence").GetInt64() > 0);
    }
}
