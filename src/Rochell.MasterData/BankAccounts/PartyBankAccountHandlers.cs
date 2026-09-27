using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.MasterData.BankAccounts;

public static class PartyBankAccountErrors
{
    public const string SupplierNotActive = "SUPPLIER_NOT_ACTIVE";
    public const string ReviewPending = "PARTY_BANK_ACCOUNT_REVIEW_PENDING";
    public const string NotInReview = "PARTY_BANK_ACCOUNT_NOT_IN_REVIEW";
    public const string SamePerson = "SAME_PERSON";
    public const string EvidenceRequired = "VERIFICATION_EVIDENCE_REQUIRED";
    public const string ReasonRequired = "REASON_REQUIRED";
}

internal static class PartyBankAccountRules
{
    public const string Aggregate = "PartyBankAccount";

    /// <summary>E-VS2-01-4: at least 20 characters of evidence (who was called, at which number, when).</summary>
    public const int MinimumEvidenceLength = 20;

    /// <summary>Requests and verifications of one supplier's accounts are serialized (one REVIEW, one VERIFIED at a time).</summary>
    public static Task LockPartyAsync(CommandContext context, Guid partyId, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('party-bank-account:' || @party, 0))",
            cancellationToken,
            ("party", partyId.ToString()));

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public sealed record ReviewRow(Guid PartyId, int Version, string Status, Guid RequestedBy);

    public sealed record SupplierRow(string Status, bool IsSupplier);

    public sealed record VersionCounts(long Pending, int LastVersion);

    public static async Task<Guid?> ScalarGuidAsync(CommandContext context, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid value ? value : null;
    }

    /// <summary>The account row, after locking its supplier; NOT_FOUND if it is not in this company.</summary>
    public static async Task<ReviewRow> ReadLockedAsync(CommandContext context, Guid accountId, CancellationToken cancellationToken)
    {
        var party = await ScalarGuidAsync(
            context,
            "SELECT party_id FROM md.party_bank_account WHERE party_bank_account_id = @id AND company_id = @c",
            cancellationToken,
            ("id", accountId),
            ("c", context.CompanyId)).ConfigureAwait(false)
            ?? throw new DomainException(MasterDataErrors.NotFound, "The supplier bank account does not exist.");
        await LockPartyAsync(context, party, cancellationToken).ConfigureAwait(false);
        return (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT party_id, version, status, requested_by FROM md.party_bank_account WHERE party_bank_account_id = @id",
            r => new ReviewRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3)),
            cancellationToken,
            ("id", accountId)).ConfigureAwait(false))!;
    }
}

[RequiresPermission("party_bank_account:request", StepUp = true)]
public sealed class RequestPartyBankAccountHandler : ICommandHandler<RequestPartyBankAccount>
{
    public string CommandType => "MasterData.RequestPartyBankAccount";

    public async Task<string> HandleAsync(RequestPartyBankAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var bankCode = BankIdentifiers.BankCode(command.BankCode);
        var accountNumber = BankIdentifiers.AccountNumber(command.AccountNumber);
        var holder = string.IsNullOrWhiteSpace(command.AccountHolder)
            ? throw new DomainException(MasterDataErrors.FieldRequired, "The account holder is required.")
            : command.AccountHolder.Trim();

        await PartyBankAccountRules.LockPartyAsync(context, command.PartyId, cancellationToken).ConfigureAwait(false);
        var supplier = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT status::text, is_supplier FROM md.party WHERE party_id = @p AND company_id = @c",
            r => new PartyBankAccountRules.SupplierRow(r.GetString(0), r.GetBoolean(1)),
            cancellationToken,
            ("p", command.PartyId),
            ("c", context.CompanyId)).ConfigureAwait(false);
        if (supplier is null || !supplier.IsSupplier)
        {
            throw new DomainException(MasterDataErrors.NotFound, "The supplier does not exist.");
        }

        if (supplier.Status != "ACTIVE")
        {
            throw new DomainException(PartyBankAccountErrors.SupplierNotActive, "Bank accounts are requested only for ACTIVE suppliers (E-VS2-02-4).");
        }

        var counts = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT count(*) FILTER (WHERE status = 'REVIEW'), coalesce(max(version), 0) FROM md.party_bank_account WHERE party_id = @p",
            r => new PartyBankAccountRules.VersionCounts(r.GetInt64(0), r.GetInt32(1)),
            cancellationToken,
            ("p", command.PartyId)).ConfigureAwait(false);
        if (counts!.Pending > 0)
        {
            throw new DomainException(PartyBankAccountErrors.ReviewPending, "This supplier already has a bank account in review; verify or reject it first (E-VS2-01-9).");
        }

        var version = counts.LastVersion + 1;
        var requester = await PartyBankAccountRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PartyBankAccountRequested",
                1,
                PartyBankAccountRules.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { partyBankAccountId = context.ResultRef, partyId = command.PartyId, version, bankCode, accountNumber, accountHolder = holder }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO md.party_bank_account (party_bank_account_id, company_id, party_id, version, bank_code, account_number, account_holder,
              status, requested_by, requested_at)
            VALUES (@id, @c, @p, @version, @bank, @number, @holder, 'REVIEW', @requester, @now)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("p", command.PartyId),
            ("version", version),
            ("bank", bankCode),
            ("number", accountNumber),
            ("holder", holder),
            ("requester", requester),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendStateAsync(PartyBankAccountRules.Aggregate, context.ResultRef, "DOCUMENT", null, "REVIEW", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyBankAccountId = context.ResultRef, status = "REVIEW", version });
    }
}

[RequiresPermission("party_bank_account:verify", StepUp = true)]
public sealed class VerifyPartyBankAccountHandler : ICommandHandler<VerifyPartyBankAccount>
{
    public string CommandType => "MasterData.VerifyPartyBankAccount";

    public async Task<string> HandleAsync(VerifyPartyBankAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var evidence = (command.Evidence ?? string.Empty).Trim();
        if (evidence.Length < PartyBankAccountRules.MinimumEvidenceLength)
        {
            throw new DomainException(PartyBankAccountErrors.EvidenceRequired, "Verification evidence must have at least 20 characters: who was called, at which number, when (E-VS2-01-4).");
        }

        var row = await PartyBankAccountRules.ReadLockedAsync(context, command.PartyBankAccountId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "REVIEW")
        {
            throw new DomainException(PartyBankAccountErrors.NotInReview, $"The account is {row.Status}; only an account in REVIEW can be verified.");
        }

        var verifier = await PartyBankAccountRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (verifier == row.RequestedBy)
        {
            throw new DomainException(PartyBankAccountErrors.SamePerson, "The requester cannot verify the account (PAY-10).");
        }

        var now = context.Clock.UtcNow;
        var payableFrom = now.AddHours(PartyBankAccounts.HoldHours);
        var superseded = await PartyBankAccountRules.ScalarGuidAsync(
            context,
            "SELECT party_bank_account_id FROM md.party_bank_account WHERE party_id = @p AND status = 'VERIFIED'",
            cancellationToken,
            ("p", row.PartyId)).ConfigureAwait(false);

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PartyBankAccountVerified",
                1,
                PartyBankAccountRules.Aggregate,
                command.PartyBankAccountId,
                2,
                JsonSerializer.Serialize(new { partyBankAccountId = command.PartyBankAccountId, partyId = row.PartyId, version = row.Version, verifiedAt = now, payableFrom, evidence, supersedes = superseded }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (superseded is { } previous)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE md.party_bank_account SET status = 'SUPERSEDED' WHERE party_bank_account_id = @id",
                cancellationToken,
                ("id", previous)).ConfigureAwait(false);
            await context.AppendStateAsync(PartyBankAccountRules.Aggregate, previous, "DOCUMENT", "VERIFIED", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE md.party_bank_account
            SET status = 'VERIFIED', verified_by = @verifier, verified_at = @now, verification_evidence = @evidence, payable_from = @payable
            WHERE party_bank_account_id = @id
            """,
            cancellationToken,
            ("verifier", verifier),
            ("now", now),
            ("evidence", evidence),
            ("payable", payableFrom),
            ("id", command.PartyBankAccountId)).ConfigureAwait(false);
        await context.AppendStateAsync(PartyBankAccountRules.Aggregate, command.PartyBankAccountId, "DOCUMENT", "REVIEW", "VERIFIED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyBankAccountId = command.PartyBankAccountId, status = "VERIFIED", payableFrom, supersedes = superseded });
    }
}

[RequiresPermission("party_bank_account:verify")]
public sealed class RejectPartyBankAccountHandler : ICommandHandler<RejectPartyBankAccount>
{
    public string CommandType => "MasterData.RejectPartyBankAccount";

    public async Task<string> HandleAsync(RejectPartyBankAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? throw new DomainException(PartyBankAccountErrors.ReasonRequired, "A rejection needs a reason (E-VS2-01-10).")
            : command.Reason.Trim();
        var row = await PartyBankAccountRules.ReadLockedAsync(context, command.PartyBankAccountId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "REVIEW")
        {
            throw new DomainException(PartyBankAccountErrors.NotInReview, $"The account is {row.Status}; only an account in REVIEW can be rejected.");
        }

        var rejecter = await PartyBankAccountRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (rejecter == row.RequestedBy)
        {
            throw new DomainException(PartyBankAccountErrors.SamePerson, "The requester cannot reject the account (E-VS2-01-10).");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PartyBankAccountRejected",
                1,
                PartyBankAccountRules.Aggregate,
                command.PartyBankAccountId,
                2,
                JsonSerializer.Serialize(new { partyBankAccountId = command.PartyBankAccountId, partyId = row.PartyId, version = row.Version, reason }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.party_bank_account SET status = 'REJECTED', rejected_by = @rejecter, rejected_at = @now, rejection_reason = @reason WHERE party_bank_account_id = @id",
            cancellationToken,
            ("rejecter", rejecter),
            ("now", context.Clock.UtcNow),
            ("reason", reason),
            ("id", command.PartyBankAccountId)).ConfigureAwait(false);
        await context.AppendStateAsync(PartyBankAccountRules.Aggregate, command.PartyBankAccountId, "DOCUMENT", "REVIEW", "REJECTED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { partyBankAccountId = command.PartyBankAccountId, status = "REJECTED" });
    }
}
