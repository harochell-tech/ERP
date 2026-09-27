using Rochell.MasterData.BankAccounts;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>VS2-02: supplier bank accounts — request, verify, reject, the 72-hour hold (PAY-06, PAY-07, PAY-10; E-VS2-01-4/8/9/10, E-VS2-02-4/5/6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PartyBankAccountTests(PostgresFixture postgres)
{
    private const string Evidence = "Llamada al 809-555-0100 el 2026-09-27, confirmó Ana Pérez (tesorería del proveedor)";

    private sealed record Actors(Guid Treasurer, Guid Controller, Guid Supplier);

    private static async Task<Actors> ActorsAsync(TestHarness h)
    {
        var supplier = await h.CreateActiveSupplierAsync("101000002", "Proveedor de prueba");
        return new Actors(await h.SessionWithRolesAsync("TESORERO"), await h.SessionWithRolesAsync("CONTROLLER"), supplier);
    }

    /// <summary>New sessions for the same people after the clock moved past the session lifetime.</summary>
    private static async Task<Actors> RefreshAsync(TestHarness h, Actors a)
    {
        async Task<Guid> Again(Guid session) => await h.CreateSessionAsync(await h.ScalarAsync<Guid>($"SELECT user_id FROM iam.session WHERE session_id = '{session}'"));
        return a with { Treasurer = await Again(a.Treasurer), Controller = await Again(a.Controller) };
    }

    private static async Task<Guid> Request(TestHarness h, Actors a, string key, string number = "9876543210", Guid? supplier = null)
        => (await h.RunAsync(new RequestPartyBankAccount(h.CompanyId, a.Treasurer, key, supplier ?? a.Supplier, "bhd", number, "Proveedor de prueba, S.R.L."), new RequestPartyBankAccountHandler())).ResultRef;

    private static Task<CommandResult> Verify(TestHarness h, Guid session, Guid account, string key, string evidence = Evidence)
        => h.RunAsync(new VerifyPartyBankAccount(h.CompanyId, session, key, account, evidence), new VerifyPartyBankAccountHandler());

    private static async Task<Payability> PayableAsync(TestHarness h, Guid supplier, Guid account)
    {
        await using var connection = await h.App.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenant = connection.CreateCommand())
        {
            tenant.Transaction = transaction;
            tenant.CommandText = $"SELECT set_config('app.company_id', '{h.CompanyId}', true)";
            await tenant.ExecuteNonQueryAsync();
        }

        return await PartyBankAccounts.PayabilityAsync(connection, transaction, h.CompanyId, supplier, account, h.Clock.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task A_request_starts_in_review_normalized_with_the_next_version()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);

        var account = await Request(h, a, "req", "987-654 3210");

        Assert.Equal("1|BHD|9876543210|REVIEW", await h.ScalarAsync<string>(
            $"SELECT version || '|' || bank_code || '|' || account_number || '|' || status FROM md.party_bank_account WHERE party_bank_account_id = '{account}'"));
    }

    [Fact]
    public async Task Requests_need_an_active_supplier_and_no_pending_review()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var draftSupplier = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.party VALUES ('{draftSupplier}', '{h.CompanyId}', 'LOCAL', '101000003', 'Proveedor en borrador', true, 'DRAFT', NULL, 1)");
        await Request(h, a, "first");

        var draft = await Assert.ThrowsAsync<DomainException>(() => Request(h, a, "draft", supplier: draftSupplier));
        var second = await Assert.ThrowsAsync<DomainException>(() => Request(h, a, "second", "1111122222"));

        Assert.Equal(PartyBankAccountErrors.SupplierNotActive, draft.Code);
        Assert.Equal(PartyBankAccountErrors.ReviewPending, second.Code);
    }

    /// <summary>PAY-10 (command half; the CHECK half is in BankSchemaTests): the requester cannot verify.</summary>
    [Trait("AcceptanceVs2", "PAY-10")]
    [Fact]
    public async Task The_requester_cannot_verify_and_evidence_is_mandatory()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var account = await Request(h, a, "req");
        // The requester (a Tesorero) cannot hold party_bank_account:verify at the same time (SoD, VS#2 §7), so the command refuses them
        // at authorization; the handler's own requester ≠ verifier check and the database CHECK are the further guards.
        var requester = await h.ScalarAsync<Guid>($"SELECT requested_by FROM md.party_bank_account WHERE party_bank_account_id = '{account}'");
        var requesterAsController = await h.CreateSessionAsync(requester);

        var self = await Assert.ThrowsAsync<DomainException>(() => Verify(h, requesterAsController, account, "self"));
        var shortEvidence = await Assert.ThrowsAsync<DomainException>(() => Verify(h, a.Controller, account, "short", "Llamé al proveedor"));

        Assert.Equal(AuthorizationErrors.NotAuthorized, self.Code); // the treasurer lacks verify: SoD keeps them apart
        Assert.Equal(PartyBankAccountErrors.EvidenceRequired, shortEvidence.Code);
        Assert.Equal("REVIEW", await h.ScalarAsync<string>($"SELECT status FROM md.party_bank_account WHERE party_bank_account_id = '{account}'"));
    }

    /// <summary>PAY-06 (payability half; release in VS2-03): verified 71 h ago → not payable; at 72 h → payable.</summary>
    [Trait("AcceptanceVs2", "PAY-06")]
    [Fact]
    public async Task A_verified_account_becomes_payable_72_calendar_hours_after_the_verification()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var a = await ActorsAsync(h);
        var account = await Request(h, a, "req");
        await Verify(h, a.Controller, account, "verify");

        var right = await PayableAsync(h, a.Supplier, account);
        clock.Advance(TimeSpan.FromHours(71));
        var at71 = await PayableAsync(h, a.Supplier, account);
        clock.Advance(TimeSpan.FromHours(1));
        var at72 = await PayableAsync(h, a.Supplier, account);
        var otherSupplier = await PayableAsync(h, await h.CreateActiveSupplierAsync("101000004", "Otro"), account);

        Assert.Equal((Payability.HoldPending, Payability.HoldPending, Payability.Payable, Payability.WrongSupplier), (right, at71, at72, otherSupplier));
    }

    /// <summary>
    /// PAY-07 (payability half): a requested change is not payable; the previous VERIFIED account stays payable until the new one is
    /// verified, which supersedes it (keeping its verification, E-VS2-01-8); the new one then waits its own 72 hours.
    /// </summary>
    [Trait("AcceptanceVs2", "PAY-07")]
    [Fact]
    public async Task A_new_account_is_not_payable_until_verified_and_held_and_the_old_one_pays_until_replaced()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var a = await ActorsAsync(h);
        var old = await Request(h, a, "old");
        await Verify(h, a.Controller, old, "verify-old");
        clock.Advance(TimeSpan.FromHours(80));
        a = await RefreshAsync(h, a);
        var changed = await Request(h, a, "new", "1111122222");

        var newInReview = await PayableAsync(h, a.Supplier, changed);
        var oldMeanwhile = await PayableAsync(h, a.Supplier, old);
        await Verify(h, a.Controller, changed, "verify-new");
        var oldAfter = await PayableAsync(h, a.Supplier, old);
        var newAfter = await PayableAsync(h, a.Supplier, changed);

        Assert.Equal((Payability.NotVerified, Payability.Payable), (newInReview, oldMeanwhile));
        Assert.Equal((Payability.NotVerified, Payability.HoldPending), (oldAfter, newAfter));
        Assert.Equal("1:SUPERSEDED:true,2:VERIFIED:true", await h.ScalarAsync<string>(
            $"SELECT string_agg(version || ':' || status || ':' || (verified_by IS NOT NULL), ',' ORDER BY version) FROM md.party_bank_account WHERE party_id = '{a.Supplier}'"));
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_and_closes_the_review()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var a = await ActorsAsync(h);
        var account = await Request(h, a, "req");
        Task<CommandResult> Reject(string key, string reason)
            => h.RunAsync(new RejectPartyBankAccount(h.CompanyId, a.Controller, key, account, reason), new RejectPartyBankAccountHandler());

        var noReason = await Assert.ThrowsAsync<DomainException>(() => Reject("no-reason", ""));
        await Reject("reject", "El titular no coincide con la razón social");
        var again = await Assert.ThrowsAsync<DomainException>(() => Verify(h, a.Controller, account, "verify"));
        var next = await Request(h, a, "next", "1111122222"); // the review slot is free again

        Assert.Equal(PartyBankAccountErrors.ReasonRequired, noReason.Code);
        Assert.Equal(PartyBankAccountErrors.NotInReview, again.Code);
        Assert.Equal("2", await h.ScalarAsync<string>($"SELECT version::text FROM md.party_bank_account WHERE party_bank_account_id = '{next}'"));
    }
}
