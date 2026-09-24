using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Finance.Policies;

[RequiresPermission("accounting_policy:prepare")]
public sealed class PrepareAccountingPolicyVersionHandler : ICommandHandler<PrepareAccountingPolicyVersion>
{
    public string CommandType => "Finance.PrepareAccountingPolicyVersion";

    public async Task<string> HandleAsync(PrepareAccountingPolicyVersion command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command.Parameters);
        if (string.IsNullOrWhiteSpace(command.Justification))
        {
            throw new DomainException(FinanceErrors.PolicyParametersInvalid, "A justification is required.");
        }

        var definitions = await DefinitionsAsync(context, command.PolicyCode, cancellationToken).ConfigureAwait(false);
        if (definitions.Count == 0)
        {
            throw new DomainException(FinanceErrors.PolicyUnknown, $"Accounting policy {command.PolicyCode} does not exist.");
        }

        var errors = Validate(definitions, command.Parameters);
        if (errors.Count > 0)
        {
            throw new DomainException(FinanceErrors.PolicyParametersInvalid, string.Join(" ", errors));
        }

        var preparer = await SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        int version;
        await using (var next = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT coalesce(max(version), 0) + 1 FROM acc.accounting_policy_version WHERE company_id = @c AND policy_code = @p",
            ("c", context.CompanyId),
            ("p", command.PolicyCode)))
        {
            version = (int)(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AccountingPolicyVersionPrepared",
                1,
                "AccountingPolicyVersion",
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { policyVersionId = context.ResultRef, policyCode = command.PolicyCode, version, effectiveFrom = command.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), parameters = command.Parameters }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO acc.accounting_policy_version (policy_version_id, policy_code, company_id, version, status, effective_from, prepared_by, justification)
            VALUES (@id, @policy, @company, @version, 'DRAFT', @from, @preparer, @justification)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("policy", command.PolicyCode),
            ("company", context.CompanyId),
            ("version", version),
            ("from", command.EffectiveFrom),
            ("preparer", preparer),
            ("justification", command.Justification.Trim())).ConfigureAwait(false);

        foreach (var (code, value) in command.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value) VALUES (@c, @v, @p, to_jsonb(CAST(@value AS text)))",
                cancellationToken,
                ("c", context.CompanyId),
                ("v", context.ResultRef),
                ("p", code),
                ("value", value.Trim())).ConfigureAwait(false);
        }

        await context.AppendStateAsync("AccountingPolicyVersion", context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { policyVersionId = context.ResultRef, version, status = "DRAFT" });
    }

    /// <summary>Every parameter of the policy exactly once, typed and within its bounds.</summary>
    internal static List<string> Validate(IReadOnlyDictionary<string, Definition> definitions, IReadOnlyDictionary<string, string> parameters)
    {
        var errors = new List<string>();
        foreach (var missing in definitions.Keys.Except(parameters.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            errors.Add($"Missing parameter {missing}.");
        }

        foreach (var (code, raw) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!definitions.TryGetValue(code, out var definition))
            {
                errors.Add($"Unknown parameter {code}.");
                continue;
            }

            var value = (raw ?? string.Empty).Trim();
            switch (definition.ValueType)
            {
                case "ENUM":
                    if (definition.AllowedValues is null || !definition.AllowedValues.Contains(value, StringComparer.Ordinal))
                    {
                        errors.Add($"{code} must be one of: {string.Join(", ", definition.AllowedValues ?? [])}.");
                    }

                    break;
                case "BOOLEAN":
                    if (value is not ("true" or "false"))
                    {
                        errors.Add($"{code} must be true or false.");
                    }

                    break;
                default:
                    var styles = definition.ValueType == "INTEGER" ? NumberStyles.None : NumberStyles.AllowDecimalPoint;
                    if (!decimal.TryParse(value, styles, CultureInfo.InvariantCulture, out var number))
                    {
                        errors.Add($"{code} must be a non-negative {(definition.ValueType == "INTEGER" ? "integer" : "decimal")} written with '.' (e.g. \"0.02\").");
                    }
                    else if ((definition.Min is not null && number < definition.Min) || (definition.Max is not null && number > definition.Max))
                    {
                        errors.Add($"{code} must be between {definition.Min} and {definition.Max}.");
                    }
                    else if (definition.ValueType == "AMOUNT" && decimal.Round(number, 4) != number)
                    {
                        errors.Add($"{code} allows at most 4 decimals.");
                    }

                    break;
            }
        }

        return errors;
    }

    private static async Task<Dictionary<string, Definition>> DefinitionsAsync(CommandContext context, string policyCode, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Definition>(StringComparer.Ordinal);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT param_code, value_type, min_value, max_value, allowed_values FROM acc.policy_parameter_definition WHERE policy_code = @p",
            ("p", policyCode));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new Definition(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<string[]>(4));
        }

        return result;
    }

    internal static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal sealed record Definition(string ValueType, decimal? Min, decimal? Max, string[]? AllowedValues);
}

[RequiresPermission("accounting_policy:approve", StepUp = true)]
public sealed class ApproveAccountingPolicyVersionHandler : ICommandHandler<ApproveAccountingPolicyVersion>
{
    public string CommandType => "Finance.ApproveAccountingPolicyVersion";

    public async Task<string> HandleAsync(ApproveAccountingPolicyVersion command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var approver = await PrepareAccountingPolicyVersionHandler.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        string policyCode;
        int version;
        DateOnly effectiveFrom;
        Guid preparedBy;
        string status;
        await using (var read = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT policy_code, version, effective_from, prepared_by, status FROM acc.accounting_policy_version WHERE company_id = @c AND policy_version_id = @v FOR UPDATE",
            ("c", context.CompanyId),
            ("v", command.PolicyVersionId)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(FinanceErrors.ConfigurationNotFound, "The policy version does not exist.");
            }

            policyCode = reader.GetString(0);
            version = reader.GetInt32(1);
            effectiveFrom = reader.GetFieldValue<DateOnly>(2);
            preparedBy = reader.GetGuid(3);
            status = reader.GetString(4);
        }

        if (status != "DRAFT")
        {
            throw new DomainException(FinanceErrors.ConfigurationNotDraft, $"The policy version is {status}.");
        }

        if (approver == preparedBy)
        {
            throw new DomainException(FinanceErrors.FourEyes, "The policy version must be approved by someone other than who prepared it.");
        }

        Guid? openId = null;
        DateOnly openFrom = default;
        await using (var open = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT policy_version_id, effective_from FROM acc.accounting_policy_version WHERE company_id = @c AND policy_code = @p AND status = 'ACTIVE' AND effective_to IS NULL FOR UPDATE",
            ("c", context.CompanyId),
            ("p", policyCode)))
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
                throw new DomainException(FinanceErrors.Overlap, $"The active version starts on {openFrom:yyyy-MM-dd}; the new one must start later.");
            }

            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE acc.accounting_policy_version SET effective_to = @to WHERE policy_version_id = @v", cancellationToken, ("to", effectiveFrom), ("v", openId.Value)).ConfigureAwait(false);
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("AccountingPolicyActivated", 1, "AccountingPolicyVersion", command.PolicyVersionId, 2, JsonSerializer.Serialize(new { policyVersionId = command.PolicyVersionId, policyCode, version, approvedBy = approver }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE acc.accounting_policy_version SET status = 'ACTIVE', approved_by = @a, approved_at = @at WHERE policy_version_id = @v",
                cancellationToken,
                ("a", approver),
                ("at", context.Clock.UtcNow),
                ("v", command.PolicyVersionId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == "23P01")
        {
            throw new DomainException(FinanceErrors.Overlap, "The version overlaps another active version of the policy.");
        }

        await context.AppendStateAsync("AccountingPolicyVersion", command.PolicyVersionId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { policyVersionId = command.PolicyVersionId, status = "ACTIVE" });
    }
}
