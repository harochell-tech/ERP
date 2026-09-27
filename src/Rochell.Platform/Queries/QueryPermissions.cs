using Rochell.Platform.Data;

namespace Rochell.Platform.Queries;

/// <summary>
/// A query that shapes its answer by a second permission (beyond the one that authorized it), e.g. E-VS2-07-3's unmasked bank
/// account numbers. Only company-wide assignments in force count.
/// </summary>
public static class QueryPermissions
{
    public static async Task<bool> HasAsync(QueryContext context, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT EXISTS (
              SELECT 1
              FROM iam.session s
              JOIN iam.role_assignment ra ON ra.user_id = s.user_id
              JOIN iam.role_permission rp ON rp.role_id = ra.role_id
              WHERE s.session_id = @session AND ra.company_id = @company AND ra.plant_id IS NULL AND rp.permission_code = @permission
                AND ra.valid_from <= @now AND (ra.valid_to IS NULL OR ra.valid_to > @now))
            """,
            ("session", context.SessionId),
            ("company", context.CompanyId),
            ("permission", permission),
            ("now", context.Clock.UtcNow));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}

/// <summary>E-VS2-02-7 / E-VS2-07-3: a bank account number as a reader without <c>bank_account_number:read</c> sees it.</summary>
public static class AccountNumbers
{
    public const string FullPermission = "bank_account_number:read";

    public static string Show(string number, bool full)
    {
        ArgumentNullException.ThrowIfNull(number);
        return full || number.Length <= 4 ? number : "••••" + number[^4..];
    }
}
