using System.Data.Common;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Finance.Configuration;

internal static class ConfigurationSql
{
    public const string ExclusionViolation = "23P01";

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}

[RequiresPermission("account_role_map:approve", StepUp = true)]
public sealed class ApproveAccountRoleMapHandler : ICommandHandler<ApproveAccountRoleMap>
{
    public string CommandType => "Finance.ApproveAccountRoleMap";

    public async Task<string> HandleAsync(ApproveAccountRoleMap command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var approver = await ConfigurationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        string role;
        string? category;
        DateOnly effectiveFrom;
        Guid preparedBy;
        string status;
        await using (var read = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT account_role, item_category, effective_from, prepared_by, status FROM fin.account_role_map WHERE company_id = @c AND map_id = @m FOR UPDATE",
            ("c", context.CompanyId),
            ("m", command.MapId)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(FinanceErrors.ConfigurationNotFound, "The account mapping does not exist.");
            }

            role = reader.GetString(0);
            category = reader.IsDBNull(1) ? null : reader.GetString(1);
            effectiveFrom = reader.GetFieldValue<DateOnly>(2);
            preparedBy = reader.GetGuid(3);
            status = reader.GetString(4);
        }

        if (status != "DRAFT")
        {
            throw new DomainException(FinanceErrors.ConfigurationNotDraft, $"The mapping is {status}.");
        }

        if (approver == preparedBy)
        {
            throw new DomainException(FinanceErrors.FourEyes, "The mapping must be approved by someone other than who prepared it.");
        }

        // Close the open ACTIVE mapping of the same role/category; it must start before the new one.
        await using (var open = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT map_id, effective_from FROM fin.account_role_map
            WHERE company_id = @c AND account_role = @r AND item_category IS NOT DISTINCT FROM @cat AND status = 'ACTIVE' AND effective_to IS NULL
            FOR UPDATE
            """,
            ("c", context.CompanyId),
            ("r", role),
            ("cat", category)))
        {
            Guid? openId = null;
            DateOnly openFrom = default;
            await using (var reader = await open.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    openId = reader.GetGuid(0);
                    openFrom = reader.GetFieldValue<DateOnly>(1);
                }
            }

            if (openId is not null)
            {
                if (openFrom >= effectiveFrom)
                {
                    throw new DomainException(FinanceErrors.Overlap, $"The current mapping starts on {openFrom:yyyy-MM-dd}; the new one must start later.");
                }

                await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.account_role_map SET effective_to = @to WHERE map_id = @m", cancellationToken, ("to", effectiveFrom), ("m", openId.Value)).ConfigureAwait(false);
            }
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("AccountRoleMapApproved", 1, "AccountRoleMap", command.MapId, 1, JsonSerializer.Serialize(new { mapId = command.MapId, accountRole = role, itemCategory = category, approvedBy = approver }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE fin.account_role_map SET status = 'ACTIVE', approved_by = @a WHERE map_id = @m",
                cancellationToken,
                ("a", approver),
                ("m", command.MapId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == ConfigurationSql.ExclusionViolation)
        {
            throw new DomainException(FinanceErrors.Overlap, "The mapping overlaps an active mapping of the same role and category.");
        }

        await context.AppendStateAsync("AccountRoleMap", command.MapId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { mapId = command.MapId, status = "ACTIVE" });
    }
}

[RequiresPermission("posting_rule:approve", StepUp = true)]
public sealed class ApprovePostingRuleVersionHandler : ICommandHandler<ApprovePostingRuleVersion>
{
    public string CommandType => "Finance.ApprovePostingRuleVersion";

    public async Task<string> HandleAsync(ApprovePostingRuleVersion command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var approver = await ConfigurationSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        Guid ruleId;
        string definitionJson;
        DateOnly effectiveFrom;
        string status;
        await using (var read = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT v.posting_rule_id, v.definition::text, v.effective_from, v.status
            FROM fin.posting_rule r JOIN fin.posting_rule_version v ON v.posting_rule_id = r.posting_rule_id
            WHERE r.code = @code AND v.version = @version
            FOR UPDATE OF v
            """,
            ("code", command.RuleCode),
            ("version", command.Version)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(FinanceErrors.ConfigurationNotFound, $"Posting rule {command.RuleCode} v{command.Version} does not exist.");
            }

            ruleId = reader.GetGuid(0);
            definitionJson = reader.GetString(1);
            effectiveFrom = reader.GetFieldValue<DateOnly>(2);
            status = reader.GetString(3);
        }

        if (status != "DRAFT")
        {
            throw new DomainException(FinanceErrors.ConfigurationNotDraft, $"The rule version is {status}.");
        }

        await ValidateDefinitionAsync(context, definitionJson, cancellationToken).ConfigureAwait(false);

        await using (var open = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT version, effective_from FROM fin.posting_rule_version WHERE posting_rule_id = @r AND status = 'ACTIVE' AND effective_to IS NULL FOR UPDATE",
            ("r", ruleId)))
        {
            int? openVersion = null;
            DateOnly openFrom = default;
            await using (var reader = await open.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    openVersion = reader.GetInt32(0);
                    openFrom = reader.GetFieldValue<DateOnly>(1);
                }
            }

            if (openVersion is not null)
            {
                if (openFrom >= effectiveFrom)
                {
                    throw new DomainException(FinanceErrors.Overlap, $"Version {openVersion} starts on {openFrom:yyyy-MM-dd}; the new one must start later.");
                }

                await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    "UPDATE fin.posting_rule_version SET effective_to = @to WHERE posting_rule_id = @r AND version = @v",
                    cancellationToken,
                    ("to", effectiveFrom),
                    ("r", ruleId),
                    ("v", openVersion.Value)).ConfigureAwait(false);
            }
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("PostingRuleVersionApproved", 1, "PostingRule", ruleId, command.Version, JsonSerializer.Serialize(new { ruleCode = command.RuleCode, version = command.Version, approvedBy = approver }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = @a WHERE posting_rule_id = @r AND version = @v",
                cancellationToken,
                ("a", approver),
                ("r", ruleId),
                ("v", command.Version)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == ConfigurationSql.ExclusionViolation)
        {
            throw new DomainException(FinanceErrors.Overlap, "The version overlaps another active version.");
        }

        await context.AppendStateAsync("PostingRuleVersion", ruleId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken, $"v{command.Version}").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ruleCode = command.RuleCode, version = command.Version, status = "ACTIVE" });
    }

    /// <summary>Structure (RuleDefinition.Parse) plus: roles exist, and control roles have a subledger (and only they).</summary>
    private static async Task ValidateDefinitionAsync(CommandContext context, string json, CancellationToken cancellationToken)
    {
        RuleDefinition definition;
        try
        {
            definition = RuleDefinition.Parse(json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new DomainException(FinanceErrors.RuleDefinitionInvalid, ex.Message);
        }

        foreach (var line in definition.Lines)
        {
            await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT is_control FROM fin.account_role WHERE role_code = @r", ("r", line.AccountRole));
            var isControl = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (isControl is not bool control)
            {
                throw new DomainException(FinanceErrors.RuleDefinitionInvalid, $"Line {line.Code}: account role {line.AccountRole} does not exist.");
            }

            if (control != (line.Subledger is not null))
            {
                throw new DomainException(FinanceErrors.RuleDefinitionInvalid, $"Line {line.Code}: control roles need a subledger and only control roles may have one.");
            }
        }
    }
}
