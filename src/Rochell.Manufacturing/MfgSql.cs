using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing;

internal static class MfgSql
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

    /// <summary>A positive quantity with at most 6 decimals.</summary>
    public static decimal Quantity(decimal value, string what)
        => value > 0m && decimal.Round(value, 6) == value
            ? value
            : throw new DomainException(ManufacturingErrors.QuantityInvalid, $"{what} must be greater than zero with at most 6 decimals.");

    public static string Code(string? code, int maxLength, string what)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length is > 0 && normalized.Length <= maxLength && char.IsAsciiLetterOrDigit(normalized[0]) && normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? normalized
            : throw new DomainException(ManufacturingErrors.FieldInvalid, $"{what} has 1 to {maxLength} letters, digits, '-' or '_', starting with a letter or digit.");
    }

    public static string Name(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= 200 ? trimmed : throw new DomainException(ManufacturingErrors.FieldRequired, "The name has 1 to 200 characters.");
    }

    /// <summary>E-MFG1-02-2: the record belongs to the plant the command is scoped to.</summary>
    public static async Task EnsurePlantAsync(CommandContext context, Guid plantId, CancellationToken cancellationToken)
    {
        if (await ScalarAsync<Guid?>(context, "SELECT plant_id FROM md.plant WHERE company_id = @c AND plant_id = @p", cancellationToken, ("c", context.CompanyId), ("p", plantId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.NotFound, "The plant does not exist.");
        }
    }
}
