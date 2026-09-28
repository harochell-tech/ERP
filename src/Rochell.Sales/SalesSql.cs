using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales;

internal static class SalesSql
{
    public static async Task<T?> ScalarAsync<T>(CommandContext context, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
        => (await ScalarAsync<Guid?>(context, "SELECT user_id FROM iam.session WHERE session_id = @s", cancellationToken, ("s", context.SessionId)).ConfigureAwait(false))!.Value;

    /// <summary>Events of an aggregate whose row has no version of its own are numbered after the ones already recorded.</summary>
    public static async Task<long> NextEventVersionAsync(CommandContext context, string aggregateType, Guid aggregateId, CancellationToken cancellationToken)
        => (await ScalarAsync<long?>(
               context,
               "SELECT max(aggregate_version) FROM core.domain_event WHERE company_id = @c AND aggregate_type = @t AND aggregate_id = @a",
               cancellationToken,
               ("c", context.CompanyId),
               ("t", aggregateType),
               ("a", aggregateId)).ConfigureAwait(false) ?? 0) + 1;

    public static Task LockAsync(CommandContext context, string key, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@k || ':' || @c, 0))",
            cancellationToken,
            ("k", key),
            ("c", context.CompanyId.ToString()));

    public static DateOnly Today(CommandContext context) => Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);

    /// <summary>A positive amount with at most <paramref name="scale"/> decimals.</summary>
    public static decimal Positive(decimal value, int scale, string what)
        => value > 0m && decimal.Round(value, scale) == value
            ? value
            : throw new DomainException(SalesErrors.AmountInvalid, $"{what} must be greater than zero with at most {scale} decimals.");

    public static string? Optional(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : throw new DomainException(SalesErrors.FieldInvalid, $"{what} has at most {maxLength} characters.");
    }
}
