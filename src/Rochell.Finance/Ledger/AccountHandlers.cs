using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Finance.Ledger;

internal static class LedgerSql
{
    public const string AccountAggregate = "Account";
    public const string JournalAggregate = "ManualJournal";

    public static readonly IReadOnlySet<string> Classes = new HashSet<string>(StringComparer.Ordinal) { "ASSET", "LIABILITY", "EQUITY", "REVENUE", "COST", "EXPENSE" };

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
        => (await ScalarAsync<Guid?>(context, "SELECT user_id FROM iam.session WHERE session_id = @s", cancellationToken, ("s", context.SessionId)).ConfigureAwait(false))!.Value;

    public static async Task<T?> ScalarAsync<T>(CommandContext context, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    public static string Class(string? value)
    {
        var c = (value ?? string.Empty).Trim().ToUpperInvariant();
        return Classes.Contains(c) ? c : throw new DomainException(LedgerErrors.AccountInvalid, "The account class must be ASSET, LIABILITY, EQUITY, REVENUE, COST or EXPENSE.");
    }

    public static string Name(string? value)
    {
        var n = (value ?? string.Empty).Trim();
        return n.Length is > 0 and <= 200 ? n : throw new DomainException(LedgerErrors.AccountInvalid, "The account name must have 1 to 200 characters.");
    }

    public sealed record AccountRow(string Code, string Status, string? Class);

    /// <summary>fin.account has no version column: its events are numbered after the ones already recorded (the row is locked).</summary>
    public static async Task<long> NextEventVersionAsync(CommandContext context, Guid accountId, CancellationToken cancellationToken)
        => (await ScalarAsync<long?>(
               context,
               "SELECT max(aggregate_version) FROM core.domain_event WHERE company_id = @c AND aggregate_type = 'Account' AND aggregate_id = @a",
               cancellationToken,
               ("c", context.CompanyId),
               ("a", accountId)).ConfigureAwait(false) ?? 0) + 1;

    public static async Task<AccountRow> LockAccountAsync(CommandContext context, Guid accountId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               "SELECT code, status, account_class FROM fin.account WHERE company_id = @c AND account_id = @a FOR UPDATE",
               r => new AccountRow(r.GetString(0), r.GetString(1), r.NullableString(2)),
               cancellationToken,
               ("c", context.CompanyId),
               ("a", accountId)).ConfigureAwait(false)
           ?? throw new DomainException(LedgerErrors.NotFound, "The account does not exist.");
}

[RequiresPermission("account:manage")]
public sealed class CreateAccountHandler : ICommandHandler<CreateAccount>
{
    public string CommandType => "Finance.CreateAccount";

    public async Task<string> HandleAsync(CreateAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim();
        if (code.Length is 0 or > 20)
        {
            throw new DomainException(LedgerErrors.AccountInvalid, "The account code must have 1 to 20 characters.");
        }

        var name = LedgerSql.Name(command.Name);
        var accountClass = LedgerSql.Class(command.AccountClass);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('account-code:' || @c || ':' || @code, 0))",
            cancellationToken,
            ("c", context.CompanyId.ToString()),
            ("code", code)).ConfigureAwait(false);
        if (await LedgerSql.ScalarAsync<Guid?>(context, "SELECT account_id FROM fin.account WHERE company_id = @c AND code = @code", cancellationToken, ("c", context.CompanyId), ("code", code)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(LedgerErrors.AccountDuplicate, $"Account {code} already exists.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AccountCreated",
                1,
                LedgerSql.AccountAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { accountId = context.ResultRef, code, name, accountClass, isControl = command.IsControl }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO fin.account (account_id, company_id, code, name, is_control, account_class, status) VALUES (@id, @c, @code, @name, @control, @class, 'ACTIVE')",
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("code", code),
            ("name", name),
            ("control", command.IsControl),
            ("class", accountClass)).ConfigureAwait(false);
        await context.AppendStateAsync(LedgerSql.AccountAggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { accountId = context.ResultRef, code, status = "ACTIVE" });
    }
}

[RequiresPermission("account:manage")]
public sealed class UpdateAccountHandler : ICommandHandler<UpdateAccount>
{
    public string CommandType => "Finance.UpdateAccount";

    public async Task<string> HandleAsync(UpdateAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var name = LedgerSql.Name(command.Name);
        var accountClass = LedgerSql.Class(command.AccountClass);
        var account = await LedgerSql.LockAccountAsync(context, command.AccountId, cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft(
                "AccountUpdated",
                1,
                LedgerSql.AccountAggregate,
                command.AccountId,
                await LedgerSql.NextEventVersionAsync(context, command.AccountId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { accountId = command.AccountId, code = account.Code, name, accountClass, previousClass = account.Class }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.account SET name = @name, account_class = @class WHERE account_id = @id",
            cancellationToken,
            ("name", name),
            ("class", accountClass),
            ("id", command.AccountId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { accountId = command.AccountId, code = account.Code });
    }
}

[RequiresPermission("account:manage")]
public sealed class DeactivateAccountHandler : ICommandHandler<DeactivateAccount>
{
    public string CommandType => "Finance.DeactivateAccount";

    public Task<string> HandleAsync(DeactivateAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AccountStatus.ChangeAsync(context, command.AccountId, "ACTIVE", "INACTIVE", CommandType, cancellationToken);
    }
}

[RequiresPermission("account:manage")]
public sealed class ActivateAccountHandler : ICommandHandler<ActivateAccount>
{
    public string CommandType => "Finance.ActivateAccount";

    public Task<string> HandleAsync(ActivateAccount command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AccountStatus.ChangeAsync(context, command.AccountId, "INACTIVE", "ACTIVE", CommandType, cancellationToken);
    }
}

internal static class AccountStatus
{
    public static async Task<string> ChangeAsync(CommandContext context, Guid accountId, string from, string to, string commandType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var account = await LedgerSql.LockAccountAsync(context, accountId, cancellationToken).ConfigureAwait(false);
        if (account.Status != from)
        {
            throw new DomainException(LedgerErrors.InvalidState, $"Account {account.Code} is {account.Status}.");
        }

        if (to == "INACTIVE" && await LedgerSql.ScalarAsync<decimal?>(
                context, "SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_id = @a", cancellationToken, ("a", accountId)).ConfigureAwait(false) is { } balance && balance != 0m)
        {
            throw new DomainException(LedgerErrors.AccountHasBalance, $"Account {account.Code} has a balance; it cannot be made inactive (E-FIN1-7).");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(to == "INACTIVE" ? "AccountDeactivated" : "AccountActivated", 1, LedgerSql.AccountAggregate, accountId, await LedgerSql.NextEventVersionAsync(context, accountId, cancellationToken).ConfigureAwait(false), JsonSerializer.Serialize(new { accountId, code = account.Code }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.account SET status = @s WHERE account_id = @id", cancellationToken, ("s", to), ("id", accountId)).ConfigureAwait(false);
        await context.AppendStateAsync(LedgerSql.AccountAggregate, accountId, "DOCUMENT", from, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { accountId, code = account.Code, status = to });
    }
}
