using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Treasury.BankAccounts;

namespace Rochell.TestInfrastructure;

/// <summary>
/// What a supplier payment needs (VS2-03): posted invoices of one supplier (their AP documents), R-09 approved, a company bank
/// account on its own control account, a treasurer, and a supplier bank account VERIFIED <c>verifiedHoursAgo</c> hours ago.
/// </summary>
public sealed record TestPayments(
    TestInvoicing Invoicing,
    Guid Supplier,
    IReadOnlyList<Guid> ApDocs,
    Guid BankAccount,
    Guid PartyBankAccount,
    Guid Treasurer,
    Guid Controller);

public static class PaymentSetup
{
    public const string R09 = "0192f001-0000-7000-8000-000000000009";

    /// <param name="invoices">1 or 2 posted invoices (6 t, or 3 t + 3 t, of sand at 1,500 plus 18 % ITBIS).</param>
    public static async Task<TestPayments> CreatePaymentSetupAsync(this TestHarness h, int invoices = 1, int verifiedHoursAgo = 73)
    {
        ArgumentNullException.ThrowIfNull(h);
        var inv = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var quantities = invoices == 1 ? new[] { 6m } : [3m, 3m];
        for (var i = 0; i < quantities.Length; i++)
        {
            var si = (await h.RunAsync(
                new RegisterSupplierInvoice(h.CompanyId, inv.Clerk, $"pay-si-{i}", inv.Purchasing.SupplierId, $"B01000000{i + 1:D2}", today, today.AddDays(30), [new(inv.PoLineId, quantities[i], 1500m)]),
                new RegisterSupplierInvoiceHandler())).ResultRef;
            await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, inv.Clerk, $"pay-si-m-{i}", si, 1), new MatchSupplierInvoiceHandler());
            await h.RunAsync(new PostSupplierInvoice(h.CompanyId, inv.Clerk, $"pay-si-p-{i}", si, 2), new PostSupplierInvoiceHandler());
        }

        var apDocs = new List<Guid>();
        await using (var command = h.Admin.CreateCommand("SELECT ap_doc_id FROM fin.ap_document WHERE company_id = @c ORDER BY original_amount, ap_doc_id"))
        {
            command.Parameters.AddWithValue("c", h.CompanyId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                apDocs.Add(reader.GetGuid(0));
            }
        }

        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R09}' AND version = 1");
        await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
        var controller = inv.Purchasing.Controller;
        var bank = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, controller, "pay-bank", "BPD", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var partyAccount = await h.VerifiedPartyBankAccountAsync(inv.Purchasing.SupplierId, treasurer, controller, 1, "9876543210", verifiedHoursAgo);
        return new TestPayments(inv, inv.Purchasing.SupplierId, apDocs, bank, partyAccount, treasurer, controller);
    }

    /// <summary>
    /// A supplier bank account version VERIFIED <paramref name="hoursAgo"/> hours ago (fixture SQL with its state history), the
    /// previous VERIFIED version, if any, SUPERSEDED. Requested by the treasurer's user, verified by the controller's user.
    /// </summary>
    public static async Task<Guid> VerifiedPartyBankAccountAsync(this TestHarness h, Guid supplier, Guid treasurerSession, Guid controllerSession, int version, string number, int hoursAgo)
    {
        ArgumentNullException.ThrowIfNull(h);
        var id = Guid.CreateVersion7();
        var requester = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", treasurerSession));
        var verifier = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", controllerSession));
        var previous = await h.ScalarAsync<Guid?>("SELECT party_bank_account_id FROM md.party_bank_account WHERE party_id = @p AND status = 'VERIFIED'", ("p", supplier));
        var states = new List<TestState> { new("PartyBankAccount", id, null, "REVIEW"), new("PartyBankAccount", id, "REVIEW", "VERIFIED") };
        var supersede = string.Empty;
        if (previous is { } old)
        {
            states.Add(new("PartyBankAccount", old, "VERIFIED", "SUPERSEDED"));
            supersede = $"UPDATE md.party_bank_account SET status = 'SUPERSEDED' WHERE party_bank_account_id = '{old}';";
        }

        await h.RunAsync(
            new TestTreasurySql(
                h.CompanyId,
                h.SessionId,
                $"party-bank-{id}",
                $"""
                INSERT INTO md.party_bank_account (party_bank_account_id, company_id, party_id, version, bank_code, account_number, account_holder, status, requested_by, requested_at)
                VALUES ('{id}', '{h.CompanyId}', '{supplier}', {version}, 'BHD', '{number}', 'Proveedor', 'REVIEW', '{requester}', now() - interval '{hoursAgo + 1} hours');
                {supersede}
                UPDATE md.party_bank_account SET status = 'VERIFIED', verified_by = '{verifier}', verified_at = now() - interval '{hoursAgo} hours',
                  verification_evidence = 'Llamada al 809-555-0100, confirmó tesorería del proveedor', payable_from = now() - interval '{hoursAgo} hours' + interval '72 hours'
                WHERE party_bank_account_id = '{id}';
                """,
                states),
            new TestTreasurySqlHandler());
        return id;
    }
}
