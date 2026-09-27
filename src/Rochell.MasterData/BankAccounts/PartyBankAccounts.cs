using System.Data.Common;
using Rochell.Platform.Data;

namespace Rochell.MasterData.BankAccounts;

/// <summary>Why a supplier bank account can or cannot receive a transfer now.</summary>
public enum Payability
{
    Payable,
    NotFound,
    WrongSupplier,
    NotVerified,
    HoldPending,
}

/// <summary>
/// "Payable" (VS#2 §4, E-VS2-8, E-VS2-02-6): VERIFIED and now ≥ payable_from (72 calendar hours after the verification). A
/// VERIFIED version stays payable while a newer one is in REVIEW, and stops at SUPERSEDED. Payment release (VS2-03) calls this
/// under its own locks.
/// </summary>
public static class PartyBankAccounts
{
    /// <summary>E-VS2-8: calendar hours between verification and the first payment. Also enforced by the database CHECK.</summary>
    public const int HoldHours = 72;

    public static async Task<Payability> PayabilityAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid companyId,
        Guid partyId,
        Guid partyBankAccountId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            connection,
            transaction,
            "SELECT party_id, status, payable_from FROM md.party_bank_account WHERE party_bank_account_id = @id AND company_id = @c",
            r => new Row(r.GetGuid(0), r.GetString(1), r.NullableUtc(2)),
            cancellationToken,
            ("id", partyBankAccountId),
            ("c", companyId)).ConfigureAwait(false);
        if (row is null)
        {
            return Payability.NotFound;
        }

        if (row.PartyId != partyId)
        {
            return Payability.WrongSupplier;
        }

        if (row.Status != "VERIFIED")
        {
            return Payability.NotVerified;
        }

        return now >= row.PayableFrom ? Payability.Payable : Payability.HoldPending;
    }

    private sealed record Row(Guid PartyId, string Status, DateTime? PayableFrom);
}
