using System.Data.Common;
using System.Text.Json;
using Rochell.MasterData.BankAccounts;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Treasury.BankAccounts;

internal static class BankAccountRules
{
    public const string Aggregate = "BankAccount";

    public sealed record GlAccount(Guid AccountId, bool IsControl, bool Mapped, bool UsedByBank);

    public sealed record BankRow(string Status, long Version);

    public sealed record OpenItems(long Payments, long Lines);
}

[RequiresPermission("bank_account:manage", StepUp = true)]
public sealed class RegisterBankAccountHandler : ICommandHandler<RegisterBankAccount>
{
    public string CommandType => "Treasury.RegisterBankAccount";

    public async Task<string> HandleAsync(RegisterBankAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var bankCode = BankIdentifiers.BankCode(command.BankCode);
        var accountNumber = BankIdentifiers.AccountNumber(command.AccountNumber);
        var glCode = (command.GlAccountCode ?? string.Empty).Trim();
        var gl = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.account_id, a.is_control,
                   EXISTS (SELECT 1 FROM fin.account_role_map m WHERE m.account_id = a.account_id),
                   EXISTS (SELECT 1 FROM fin.bank_account b WHERE b.gl_account_id = a.account_id)
            FROM fin.account a WHERE a.company_id = @c AND a.code = @code
            """,
            r => new BankAccountRules.GlAccount(r.GetGuid(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("code", glCode)).ConfigureAwait(false)
            ?? throw new DomainException(TreasuryErrors.GlAccountNotFound, $"Account {glCode} is not in the chart of accounts.");
        if (!gl.IsControl || gl.Mapped || gl.UsedByBank)
        {
            throw new DomainException(
                TreasuryErrors.GlAccountNotEligible,
                "A bank account needs its own control account, used by no other bank account and by no role map (E-VS2-01-1).");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "BankAccountRegistered",
                1,
                BankAccountRules.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { bankAccountId = context.ResultRef, bankCode, accountNumber, currency = "DOP", glAccountId = gl.AccountId, glAccountCode = glCode }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.bank_account (bank_account_id, company_id, bank_code, account_number, currency, gl_account_id, status, version)
                VALUES (@id, @c, @bank, @number, 'DOP', @gl, 'ACTIVE', 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("bank", bankCode),
                ("number", accountNumber),
                ("gl", gl.AccountId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(TreasuryErrors.BankAccountDuplicate, $"Account {bankCode} {accountNumber} is already registered, or its GL account is taken.");
        }

        await context.AppendStateAsync(BankAccountRules.Aggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { bankAccountId = context.ResultRef, status = "ACTIVE", version = 1 });
    }
}

[RequiresPermission("bank_account:manage", StepUp = true)]
public sealed class CloseBankAccountHandler : ICommandHandler<CloseBankAccount>
{
    public string CommandType => "Treasury.CloseBankAccount";

    public async Task<string> HandleAsync(CloseBankAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? throw new DomainException(TreasuryErrors.ReasonRequired, "Closing a bank account needs a reason (E-VS2-02-3).")
            : command.Reason.Trim();
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, version FROM fin.bank_account WHERE bank_account_id = @id AND company_id = @c FOR UPDATE",
            r => new BankAccountRules.BankRow(r.GetString(0), r.GetInt64(1)),
            cancellationToken,
            ("id", command.BankAccountId),
            ("c", context.CompanyId)).ConfigureAwait(false)
            ?? throw new DomainException(TreasuryErrors.BankAccountNotFound, "The bank account does not exist.");
        if (row.Version != command.ExpectedVersion)
        {
            throw new DomainException(TreasuryErrors.VersionConflict, $"The bank account is at version {row.Version}, not {command.ExpectedVersion}.");
        }

        if (row.Status != "ACTIVE")
        {
            throw new DomainException(TreasuryErrors.BankAccountNotActive, "The bank account is already closed.");
        }

        // E-VS2-02-3: payments not yet cleared, voided or reversed, and statement lines not yet matched or recognized, keep it open.
        var open = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT (SELECT count(*) FROM fin.payment WHERE bank_account_id = @id AND status::text IN ('PREPARED', 'RELEASED')),
                   (SELECT count(*) FROM fin.bank_statement_line WHERE bank_account_id = @id AND status = 'UNMATCHED')
            """,
            r => new BankAccountRules.OpenItems(r.GetInt64(0), r.GetInt64(1)),
            cancellationToken,
            ("id", command.BankAccountId)).ConfigureAwait(false);
        if (open!.Payments > 0 || open.Lines > 0)
        {
            throw new DomainException(
                TreasuryErrors.BankAccountHasOpenItems,
                $"The bank account has {open.Payments} payment(s) prepared or released and {open.Lines} unmatched statement line(s).");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("BankAccountClosed", 1, BankAccountRules.Aggregate, command.BankAccountId, version, JsonSerializer.Serialize(new { bankAccountId = command.BankAccountId, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.bank_account SET status = 'CLOSED', version = @version WHERE bank_account_id = @id",
            cancellationToken,
            ("version", version),
            ("id", command.BankAccountId)).ConfigureAwait(false);
        await context.AppendStateAsync(BankAccountRules.Aggregate, command.BankAccountId, "DOCUMENT", "ACTIVE", "CLOSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { bankAccountId = command.BankAccountId, status = "CLOSED", version });
    }
}
