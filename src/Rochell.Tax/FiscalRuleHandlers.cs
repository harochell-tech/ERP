using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;

namespace Rochell.Tax;

internal static class FiscalRuleStore
{
    public const string Aggregate = "FiscalRuleVersion";

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static async Task<RuleVersion> LockVersionAsync(CommandContext context, Guid versionId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT v.rule_version_id, v.rule_id, r.code, r.rule_kind, v.version, v.definition::text, v.effective_from, v.effective_to, v.status, v.configured_by, v.row_version
            FROM tax.fiscal_rule_version v JOIN tax.fiscal_rule r ON r.rule_id = v.rule_id
            WHERE v.company_id = @c AND v.rule_version_id = @v
            FOR UPDATE OF v
            """,
            ("c", context.CompanyId),
            ("v", versionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RuleVersion(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
                reader.GetFieldValue<DateOnly>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7), reader.GetString(8), reader.GetGuid(9), reader.GetInt64(10))
            : throw new DomainException(TaxErrors.VersionNotFound, "The fiscal rule version does not exist.");
    }

    /// <summary>E-PR12-4: a version under configuration is READY exactly when it has a source and its latest test run passed.</summary>
    public static async Task<string> RecomputeStatusAsync(CommandContext context, RuleVersion version, string commandType, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT EXISTS (SELECT 1 FROM tax.fiscal_rule_version_source WHERE rule_version_id = @v)
               AND coalesce((SELECT passed FROM tax.fiscal_rule_test_run
                             WHERE rule_version_id = @v AND environment = (SELECT environment FROM core.deployment_environment)
                             ORDER BY executed_at DESC, test_run_id DESC LIMIT 1), false)
            """,
            ("v", version.Id));
        var ready = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        var desired = ready ? FiscalRuleStatus.Ready : FiscalRuleStatus.BlockedPendingSource;
        if (desired == version.Status)
        {
            return desired;
        }

        var newRowVersion = version.RowVersion + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("FiscalRuleVersionStatusChanged", 1, Aggregate, version.Id, newRowVersion, JsonSerializer.Serialize(new { ruleVersionId = version.Id, status = desired }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, version.Id, "DOCUMENT", version.Status, desired, commandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE tax.fiscal_rule_version SET status = @s, row_version = @rv WHERE rule_version_id = @v",
            cancellationToken,
            ("s", desired),
            ("rv", newRowVersion),
            ("v", version.Id)).ConfigureAwait(false);
        return desired;
    }

    public static void RequireConfigurable(RuleVersion version)
    {
        if (version.Status is not (FiscalRuleStatus.BlockedPendingSource or FiscalRuleStatus.Ready))
        {
            throw new DomainException(TaxErrors.VersionNotConfigurable, $"The version is {version.Status}; configure a new version instead.");
        }
    }

    public static string RequireText(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? throw new DomainException(TaxErrors.SourceInvalid, $"{field} is required.") : value.Trim();
}

internal sealed record RuleVersion(
    Guid Id, Guid RuleId, string RuleCode, string RuleKind, int Version, string Definition, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status, Guid ConfiguredBy, long RowVersion);

[RequiresPermission("fiscal_rule_source:register")]
public sealed class RegisterFiscalSourceHandler : ICommandHandler<RegisterFiscalSource>
{
    public string CommandType => "Tax.RegisterFiscalSource";

    public async Task<string> HandleAsync(RegisterFiscalSource command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var hashText = FiscalRuleStore.RequireText(command.FileSha256, "FileSha256").ToLowerInvariant();
        if (hashText.Length != 64 || !hashText.All(Uri.IsHexDigit))
        {
            throw new DomainException(TaxErrors.SourceInvalid, "FileSha256 must be the 64 hexadecimal characters of the document's SHA-256.");
        }

        if (command.Environment is not (FiscalSourceEnvironments.Test or FiscalSourceEnvironments.Production))
        {
            throw new DomainException(TaxErrors.SourceInvalid, "Environment must be TEST or PRODUCTION (P-7).");
        }

        var consultedAt = Platform.Time.Precision.ToMicroseconds(command.ConsultedAt);
        if (consultedAt > context.Clock.UtcNow)
        {
            throw new DomainException(TaxErrors.SourceInvalid, "The consultation time cannot be in the future.");
        }

        if (command.EffectiveTo is { } to && to <= command.EffectiveFrom)
        {
            throw new DomainException(TaxErrors.SourceInvalid, "EffectiveTo must be after EffectiveFrom.");
        }

        var registrar = await FiscalRuleStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var sourceId = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "FiscalSourceRegistered",
                1,
                "FiscalRuleSource",
                sourceId,
                1,
                JsonSerializer.Serialize(new { sourceId, officialSource = command.OfficialSource, documentTitle = command.DocumentTitle, documentVersion = command.DocumentVersion, fileSha256 = hashText, environment = command.Environment }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        _ = eventId;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.fiscal_rule_source (source_id, company_id, official_source, document_title, document_version, publication_date, consulted_at,
                                                effective_from, effective_to, url_or_reference, file_object_key, file_hash, approved_by, approved_at, environment)
            VALUES (@id, @c, @official, @title, @docVersion, @published, @consulted, @from, @to, @url, @file, @hash, @by, @at, @environment)
            """,
            cancellationToken,
            ("id", sourceId),
            ("c", context.CompanyId),
            ("official", FiscalRuleStore.RequireText(command.OfficialSource, "OfficialSource")),
            ("title", FiscalRuleStore.RequireText(command.DocumentTitle, "DocumentTitle")),
            ("docVersion", FiscalRuleStore.RequireText(command.DocumentVersion, "DocumentVersion")),
            ("published", command.PublicationDate),
            ("consulted", consultedAt),
            ("from", command.EffectiveFrom),
            ("to", command.EffectiveTo),
            ("url", FiscalRuleStore.RequireText(command.UrlOrReference, "UrlOrReference")),
            ("file", FiscalRuleStore.RequireText(command.FileReference, "FileReference")),
            ("hash", Convert.FromHexString(hashText)),
            ("by", registrar),
            ("at", context.Clock.UtcNow),
            ("environment", command.Environment)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { sourceId });
    }
}

[RequiresPermission("fiscal_rule:configure")]
public sealed class ConfigureFiscalRuleVersionHandler : ICommandHandler<ConfigureFiscalRuleVersion>
{
    public string CommandType => "Tax.ConfigureFiscalRuleVersion";

    public async Task<string> HandleAsync(ConfigureFiscalRuleVersion command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var definition = FiscalRuleDefinition.Parse(command.RuleKind, command.Definition);
        var code = string.IsNullOrWhiteSpace(command.RuleCode) ? throw new DomainException(TaxErrors.FiscalRuleInvalid, "RuleCode is required.") : command.RuleCode.Trim();
        var canonical = JsonCanonicalizer.Canonicalize(command.Definition);

        // The rule row, created on its first version; its kind never changes.
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO tax.fiscal_rule (rule_id, company_id, code, rule_kind) VALUES (@id, @c, @code, @kind) ON CONFLICT (company_id, code) DO NOTHING",
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("code", code),
            ("kind", definition.Kind)).ConfigureAwait(false);
        // Versions of one rule are numbered one at a time. The application may not lock fiscal_rule rows (FOR UPDATE needs the
        // UPDATE privilege it deliberately lacks on this append-only table), so the rule code is serialized with an advisory lock.
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('fiscal_rule:' || @c::text || ':' || @code, 0))",
            cancellationToken,
            ("c", context.CompanyId),
            ("code", code)).ConfigureAwait(false);
        Guid ruleId;
        await using (var rule = Sql.Command(context.Connection, context.Transaction, "SELECT rule_id, rule_kind FROM tax.fiscal_rule WHERE company_id = @c AND code = @code", ("c", context.CompanyId), ("code", code)))
        await using (var reader = await rule.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ruleId = reader.GetGuid(0);
            if (reader.GetString(1) != definition.Kind)
            {
                throw new DomainException(TaxErrors.RuleKindMismatch, $"Rule {code} is {reader.GetString(1)}, not {definition.Kind}.");
            }
        }

        int version;
        await using (var next = Sql.Command(context.Connection, context.Transaction, "SELECT coalesce(max(version), 0) + 1 FROM tax.fiscal_rule_version WHERE rule_id = @r", ("r", ruleId)))
        {
            version = (int)(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var configurer = await FiscalRuleStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var versionId = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "FiscalRuleVersionConfigured",
                1,
                FiscalRuleStore.Aggregate,
                versionId,
                1,
                JsonSerializer.Serialize(new { ruleVersionId = versionId, ruleCode = code, ruleKind = definition.Kind, version, effectiveFrom = command.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(FiscalRuleStore.Aggregate, versionId, "DOCUMENT", null, FiscalRuleStatus.BlockedPendingSource, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.fiscal_rule_version (rule_version_id, company_id, rule_id, version, definition, effective_from, status, configured_by, configured_at, row_version)
            VALUES (@id, @c, @rule, @version, CAST(@definition AS jsonb), @from, 'BLOCKED_PENDING_SOURCE', @by, @at, 1)
            """,
            cancellationToken,
            ("id", versionId),
            ("c", context.CompanyId),
            ("rule", ruleId),
            ("version", version),
            ("definition", canonical),
            ("from", command.EffectiveFrom),
            ("by", configurer),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ruleVersionId = versionId, ruleCode = code, version, status = FiscalRuleStatus.BlockedPendingSource });
    }
}

[RequiresPermission("fiscal_rule:configure")]
public sealed class LinkFiscalSourceHandler : ICommandHandler<LinkFiscalSource>
{
    public string CommandType => "Tax.LinkFiscalSource";

    public async Task<string> HandleAsync(LinkFiscalSource command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var version = await FiscalRuleStore.LockVersionAsync(context, command.RuleVersionId, cancellationToken).ConfigureAwait(false);
        FiscalRuleStore.RequireConfigurable(version);
        await using (var source = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT effective_from <= @d AND (effective_to IS NULL OR effective_to > @d) FROM tax.fiscal_rule_source WHERE company_id = @c AND source_id = @s",
            ("c", context.CompanyId),
            ("s", command.SourceId),
            ("d", version.EffectiveFrom)))
        {
            switch (await source.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            {
                case null:
                    throw new DomainException(TaxErrors.SourceNotFound, "The fiscal source does not exist.");
                case false:
                    throw new DomainException(TaxErrors.SourceNotEffective, "The source is not in force on the version's effective date.");
            }
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.fiscal_rule_version_source (company_id, rule_version_id, source_id, linked_by, linked_at)
            VALUES (@c, @v, @s, @by, @at) ON CONFLICT DO NOTHING
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("v", version.Id),
            ("s", command.SourceId),
            ("by", await FiscalRuleStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        var status = await FiscalRuleStore.RecomputeStatusAsync(context, version, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ruleVersionId = version.Id, status });
    }
}

[RequiresPermission("fiscal_rule:configure")]
public sealed class RunFiscalRuleTestsHandler : ICommandHandler<RunFiscalRuleTests>
{
    public string CommandType => "Tax.RunFiscalRuleTests";

    public async Task<string> HandleAsync(RunFiscalRuleTests command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.Cases is null || command.Cases.Count == 0)
        {
            throw new DomainException(TaxErrors.CasesRequired, "A test run needs at least one case.");
        }

        var version = await FiscalRuleStore.LockVersionAsync(context, command.RuleVersionId, cancellationToken).ConfigureAwait(false);
        FiscalRuleStore.RequireConfigurable(version);
        var rule = new ApplicableRule(version.Id, version.RuleCode, FiscalRuleDefinition.Parse(version.RuleKind, version.Definition));

        var failures = new List<string>();
        var results = new List<object>();
        foreach (var testCase in command.Cases)
        {
            var line = new TaxableLine(Guid.Empty, testCase.ItemCategory, testCase.NetAmount);
            var actual = (version.RuleKind == FiscalRuleKinds.PurchaseItbis
                    ? TaxCalculator.Itbis(rule, line)
                    : TaxCalculator.Withholding(rule, line, testCase.PartyType, testCase.ItbisAmount)) is { } tax
                ? new[] { new ExpectedTax(tax.TaxCode, tax.Amount, tax.Effect) }
                : [];
            var expected = testCase.Expected ?? [];
            var matches = actual.Length == expected.Count
                && actual.All(a => expected.Any(e => e.TaxCode == a.TaxCode && e.Effect == a.Effect && e.Amount == a.Amount));
            if (!matches)
            {
                failures.Add(testCase.CaseId);
            }

            results.Add(new
            {
                caseId = testCase.CaseId,
                passed = matches,
                actual = actual.Select(a => new { taxCode = a.TaxCode, amount = a.Amount.ToString("0.00", CultureInfo.InvariantCulture), effect = a.Effect }),
            });
        }

        var passed = failures.Count == 0;
        var resultHash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(results))));

        // E-PR12-5: the run belongs to this deployment's environment; an uninitialized deployment is a configuration error,
        // never a run silently left unrecorded.
        string environment;
        await using (var env = Sql.Command(context.Connection, context.Transaction, "SELECT environment FROM core.deployment_environment"))
        {
            environment = await env.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("The deployment environment is not initialized; run `rochell-migrate init-environment TEST|PRODUCTION` first.");
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.fiscal_rule_test_run (test_run_id, company_id, rule_version_id, environment, passed, cases, result_hash, executed_by, executed_at)
            VALUES (@id, @c, @v, @env, @passed, @cases, @hash, @by, @at)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("env", environment),
            ("c", context.CompanyId),
            ("v", version.Id),
            ("passed", passed),
            ("cases", command.Cases.Count),
            ("hash", resultHash),
            ("by", await FiscalRuleStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        var status = await FiscalRuleStore.RecomputeStatusAsync(context, version, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { testRunId = context.ResultRef, passed, failures, status, resultHash = Convert.ToHexStringLower(resultHash) });
    }
}

[RequiresPermission("fiscal_rule:activate", StepUp = true)]
public sealed class ActivateFiscalRuleVersionHandler : ICommandHandler<ActivateFiscalRuleVersion>
{
    public string CommandType => "Tax.ActivateFiscalRuleVersion";

    public async Task<string> HandleAsync(ActivateFiscalRuleVersion command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var version = await FiscalRuleStore.LockVersionAsync(context, command.RuleVersionId, cancellationToken).ConfigureAwait(false);
        if (version.Status != FiscalRuleStatus.Ready)
        {
            throw new DomainException(TaxErrors.VersionNotReady, $"The version is {version.Status}; it needs an official source and a passing test run.");
        }

        var activator = await FiscalRuleStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (activator == version.ConfiguredBy)
        {
            throw new DomainException(TaxErrors.ActivatorIsConfigurer, "The person who configured the version cannot activate it.");
        }

        // P-7 condition 2 (E-PR19-9): in production a version needs at least one PRODUCTION source. The database gate enforces
        // the same rule; checking here first turns it into a business rejection.
        await using (var production = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT core.current_environment() IS DISTINCT FROM 'PRODUCTION' OR EXISTS (
              SELECT 1 FROM tax.fiscal_rule_version_source vs JOIN tax.fiscal_rule_source s ON s.source_id = vs.source_id
              WHERE vs.rule_version_id = @v AND s.environment = 'PRODUCTION')
            """,
            ("v", command.RuleVersionId)))
        {
            if (await production.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new DomainException(TaxErrors.ProductionSourceRequired, "In production a fiscal rule version is activated only with at least one PRODUCTION source (P-7).");
            }
        }

        if (version.RuleKind == FiscalRuleKinds.PurchaseItbis)
        {
            await using var other = Sql.Command(
                context.Connection,
                context.Transaction,
                """
                SELECT EXISTS (
                  SELECT 1 FROM tax.fiscal_rule_version v JOIN tax.fiscal_rule r ON r.rule_id = v.rule_id
                  WHERE r.company_id = @c AND r.rule_kind = 'PURCHASE_ITBIS' AND r.rule_id <> @rule AND v.status = 'ACTIVE'
                    AND daterange(v.effective_from, v.effective_to, '[)') && daterange(@from, NULL, '[)'))
                """,
                ("c", context.CompanyId),
                ("rule", version.RuleId),
                ("from", version.EffectiveFrom));
            if (await other.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new DomainException(TaxErrors.AnotherItbisRuleActive, "Another purchase ITBIS rule is active; only one may apply at a time.");
            }
        }

        // The open predecessor of the same rule ends where this version starts (or is retired when fully superseded).
        await using (var predecessor = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT rule_version_id, effective_from, row_version FROM tax.fiscal_rule_version WHERE rule_id = @rule AND status = 'ACTIVE' AND effective_to IS NULL FOR UPDATE",
            ("rule", version.RuleId)))
        {
            Guid? previousId = null;
            DateOnly previousFrom = default;
            long previousRowVersion = 0;
            await using (var reader = await predecessor.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    previousId = reader.GetGuid(0);
                    previousFrom = reader.GetFieldValue<DateOnly>(1);
                    previousRowVersion = reader.GetInt64(2);
                }
            }

            if (previousId is not null)
            {
                var retire = previousFrom >= version.EffectiveFrom;
                var eventId = await context.AppendEventAsync(
                    new EventDraft(
                        retire ? "FiscalRuleVersionRetired" : "FiscalRuleVersionClosed",
                        1,
                        FiscalRuleStore.Aggregate,
                        previousId.Value,
                        previousRowVersion + 1,
                        JsonSerializer.Serialize(new { ruleVersionId = previousId, supersededBy = version.Id }),
                        Publish: true),
                    cancellationToken).ConfigureAwait(false);
                if (retire)
                {
                    await context.AppendStateAsync(FiscalRuleStore.Aggregate, previousId.Value, "DOCUMENT", FiscalRuleStatus.Active, FiscalRuleStatus.Retired, CommandType, eventId, cancellationToken).ConfigureAwait(false);
                }

                await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    retire
                        ? "UPDATE tax.fiscal_rule_version SET status = 'RETIRED', row_version = row_version + 1 WHERE rule_version_id = @p"
                        : "UPDATE tax.fiscal_rule_version SET effective_to = @to, row_version = row_version + 1 WHERE rule_version_id = @p",
                    cancellationToken,
                    ("p", previousId.Value),
                    ("to", version.EffectiveFrom)).ConfigureAwait(false);
            }
        }

        var newRowVersion = version.RowVersion + 1;
        var activatedEvent = await context.AppendEventAsync(
            new EventDraft(
                "FiscalRuleVersionActivated",
                1,
                FiscalRuleStore.Aggregate,
                version.Id,
                newRowVersion,
                JsonSerializer.Serialize(new { ruleVersionId = version.Id, ruleCode = version.RuleCode, activatedBy = activator }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(FiscalRuleStore.Aggregate, version.Id, "DOCUMENT", FiscalRuleStatus.Ready, FiscalRuleStatus.Active, CommandType, activatedEvent, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE tax.fiscal_rule_version SET status = 'ACTIVE', activated_by = @by, activated_at = @at, row_version = @rv WHERE rule_version_id = @v",
            cancellationToken,
            ("by", activator),
            ("at", context.Clock.UtcNow),
            ("rv", newRowVersion),
            ("v", version.Id)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ruleVersionId = version.Id, status = FiscalRuleStatus.Active });
    }
}
