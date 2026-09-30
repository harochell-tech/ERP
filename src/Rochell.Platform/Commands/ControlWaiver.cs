using Rochell.Platform.Data;

namespace Rochell.Platform.Commands;

/// <summary>
/// E-ADM-2-3/4: a command's "decider ≠ preparer" check is waived for a superadministrator (an unexpired SUPERADMIN assignment in the
/// company). A waiver marks the transaction, so the command log and the state history record it (E-ADM-2-5).
/// </summary>
public static class ControlWaiver
{
    /// <summary>True, and the transaction marked, when the acting user's four-eyes controls are waived.</summary>
    public static async Task<bool> WaivedAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT CASE WHEN iam.controls_waived(@c, s.user_id, @now) THEN set_config('app.controls_waived', 'true', true) = 'true' ELSE false END
            FROM iam.session s WHERE s.session_id = @s
            """,
            ("c", context.CompanyId),
            ("s", context.SessionId),
            ("now", context.Clock.UtcNow));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
