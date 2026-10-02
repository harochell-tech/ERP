using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Tax.Packs;

/// <summary>An official document of the pack: what `RegisterFiscalSource` takes, with the file whose SHA-256 identifies it.</summary>
public sealed record PackSource(string Key, string File, string OfficialSource, string DocumentTitle, string DocumentVersion, DateOnly PublicationDate, DateOnly EffectiveFrom, string Url);

public sealed record PackExpectedTax(string TaxCode, string Amount, string Effect);

public sealed record PackCase(string CaseId, string PartyType, string ItemCategory, string NetAmount, string ItbisAmount, IReadOnlyList<PackExpectedTax> Expected);

/// <summary>A rule version to prepare: its definition as the screens' JSON, the sources (by key) that back it and its regression cases.</summary>
public sealed record PackRule(string Code, string Kind, DateOnly EffectiveFrom, JsonObject Definition, IReadOnlyList<string> Sources, IReadOnlyList<PackCase> Cases);

/// <summary>E-CFG-3: fiscal sources and rules as a file in the repository, reviewed before it is loaded.</summary>
public sealed record FiscalRulePack(string Pack, IReadOnlyList<PackSource> Sources, IReadOnlyList<PackRule> Rules)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static FiscalRulePack Parse(string json)
    {
        var pack = JsonSerializer.Deserialize<FiscalRulePack>(json, Options) ?? throw new FormatException("The pack is empty.");
        if (string.IsNullOrWhiteSpace(pack.Pack) || pack.Sources is null || pack.Rules is null)
        {
            throw new FormatException("The pack needs its name, its sources and its rules.");
        }

        var keys = pack.Sources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != pack.Sources.Count || pack.Rules.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count() != pack.Rules.Count)
        {
            throw new FormatException("Source keys and rule codes are unique in a pack.");
        }

        foreach (var rule in pack.Rules)
        {
            if (rule.Sources is not { Count: > 0 } || rule.Sources.Any(s => !keys.Contains(s)))
            {
                throw new FormatException($"Rule {rule.Code} names a source that the pack does not have, or none.");
            }
        }

        return pack;
    }
}

/// <summary>One line of what the load did, for the operator and the log.</summary>
public sealed record PackStep(string Subject, string Outcome, string Detail);

/// <summary>
/// E-CFG-1…6: loads a pack through the same commands as the screens, on the session given (the configuration-load service
/// identity). It is safe to run again: a source already registered (same SHA-256) is reused, a rule version already prepared or
/// active with the same definition and start date is reused, and a rule configured differently is left alone and reported — the
/// load never overrides what a person configured, and it never activates.
/// </summary>
public sealed class FiscalRulePackLoader(CommandPipeline pipeline, DbDataSource dataSource)
{
    private sealed record Version(Guid Id, string Status, string Definition, DateOnly EffectiveFrom, bool TestsPassed);

    /// <param name="readFile">Returns the bytes of a source's file, or null when it is not there.</param>
    /// <param name="environment">TEST or PRODUCTION: what the registered sources are for (P-7).</param>
    public async Task<IReadOnlyList<PackStep>> LoadAsync(
        Guid companyId, Guid sessionId, FiscalRulePack pack, Func<string, byte[]?> readFile, string environment, DateTime consultedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(readFile);
        var steps = new List<PackStep>();
        var run = Guid.CreateVersion7().ToString("N");
        string Key(string step) => $"fiscal-pack:{pack.Pack}:{step}:{run}";

        // Sources: one per official file, identified by its SHA-256.
        var sourceIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var source in pack.Sources)
        {
            var bytes = readFile(source.File);
            if (bytes is null)
            {
                steps.Add(new PackStep(source.Key, "MISSING_FILE", $"{source.File} is not in the documents folder: the source was not registered (E-CFG-4)."));
                continue;
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var existing = await ScalarAsync<Guid?>(
                companyId, "SELECT source_id FROM tax.fiscal_rule_source WHERE company_id = @c AND file_hash = @sha AND environment = @env ORDER BY approved_at LIMIT 1", cancellationToken,
                ("c", companyId), ("sha", Convert.FromHexString(sha)), ("env", environment)).ConfigureAwait(false);
            if (existing is { } known)
            {
                sourceIds[source.Key] = known;
                steps.Add(new PackStep(source.Key, "EXISTS", $"Already registered with SHA-256 {sha}."));
                continue;
            }

            var registered = await pipeline.ExecuteAsync(
                new RegisterFiscalSource(
                    companyId, sessionId, Key("source-" + source.Key), source.OfficialSource, source.DocumentTitle, source.DocumentVersion, source.PublicationDate, consultedAt, source.EffectiveFrom,
                    null, source.Url, source.File, sha, environment),
                new RegisterFiscalSourceHandler(), Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
            sourceIds[source.Key] = registered.ResultRef;
            steps.Add(new PackStep(source.Key, "REGISTERED", $"{source.DocumentTitle} ({environment}), SHA-256 {sha}."));
        }

        foreach (var rule in pack.Rules)
        {
            if (rule.Sources.Any(s => !sourceIds.ContainsKey(s)))
            {
                steps.Add(new PackStep(rule.Code, "SKIPPED", "A source of the rule is missing."));
                continue;
            }

            var definition = rule.Definition.ToJsonString();
            var versions = await VersionsAsync(companyId, rule.Code, cancellationToken).ConfigureAwait(false);
            var live = versions.Where(v => v.Status is "BLOCKED_PENDING_SOURCE" or "READY" or "ACTIVE").ToList();
            var same = live.FirstOrDefault(v => v.EffectiveFrom == rule.EffectiveFrom && Canonical(v.Definition) == Canonical(definition));
            if (same is null && live.Count > 0)
            {
                steps.Add(new PackStep(rule.Code, "DIFFERENT", $"The rule already has a version ({live[0].Status}, from {live[0].EffectiveFrom:yyyy-MM-dd}) that differs from the pack: left as it is."));
                continue;
            }

            if (same is { Status: "ACTIVE" })
            {
                steps.Add(new PackStep(rule.Code, "ACTIVE", "Already active with the pack's definition."));
                continue;
            }

            Guid versionId;
            if (same is null)
            {
                versionId = (await pipeline.ExecuteAsync(
                    new ConfigureFiscalRuleVersion(companyId, sessionId, Key("rule-" + rule.Code), rule.Code, rule.Kind, definition, rule.EffectiveFrom), new ConfigureFiscalRuleVersionHandler(),
                    Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false)).ResultRef;
            }
            else
            {
                versionId = same.Id;
            }

            foreach (var key in rule.Sources)
            {
                var linked = await ScalarAsync<bool?>(
                    companyId, "SELECT EXISTS (SELECT 1 FROM tax.fiscal_rule_version_source WHERE rule_version_id = @v AND source_id = @s)", cancellationToken, ("v", versionId), ("s", sourceIds[key]))
                    .ConfigureAwait(false);
                if (linked != true)
                {
                    await pipeline.ExecuteAsync(
                        new LinkFiscalSource(companyId, sessionId, Key($"link-{rule.Code}-{key}"), versionId, sourceIds[key]), new LinkFiscalSourceHandler(), Guid.CreateVersion7(), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (rule.Cases.Count > 0 && same is not { TestsPassed: true })
            {
                var cases = rule.Cases.Select(c => new FiscalTestCase(
                    c.CaseId, c.PartyType, c.ItemCategory, Amount(c.NetAmount), Amount(c.ItbisAmount), [.. c.Expected.Select(e => new ExpectedTax(e.TaxCode, Amount(e.Amount), e.Effect))])).ToList();
                await pipeline.ExecuteAsync(new RunFiscalRuleTests(companyId, sessionId, Key("tests-" + rule.Code), versionId, cases), new RunFiscalRuleTestsHandler(), Guid.CreateVersion7(), cancellationToken)
                    .ConfigureAwait(false);
            }

            var status = (await VersionsAsync(companyId, rule.Code, cancellationToken).ConfigureAwait(false)).Single(v => v.Id == versionId).Status;
            steps.Add(new PackStep(
                rule.Code, status, status == "READY" ? "Prepared with its source and tests: a person activates it (E-CFG-5)." : "Not READY: see its tests on Configuración › Reglas fiscales."));
        }

        return steps;
    }

    private static decimal Amount(string text) => decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    /// <summary>The definition with its members sorted, so that two JSON texts of the same content compare equal.</summary>
    private static string Canonical(string json)
    {
        static JsonNode? Sort(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
            JsonArray a => new JsonArray([.. a.Select(i => Sort(i?.DeepClone()))]),
            _ => node?.DeepClone(),
        };

        return Sort(JsonNode.Parse(json))!.ToJsonString();
    }

    private async Task<List<Version>> VersionsAsync(Guid companyId, string code, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @company, true)", cancellationToken, ("company", companyId.ToString())).ConfigureAwait(false);
        return await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT v.rule_version_id, v.status, v.definition::text, v.effective_from,
                   coalesce((SELECT t.passed FROM tax.fiscal_rule_test_run t WHERE t.rule_version_id = v.rule_version_id ORDER BY t.executed_at DESC LIMIT 1), false)
            FROM tax.fiscal_rule r JOIN tax.fiscal_rule_version v ON v.rule_id = r.rule_id
            WHERE r.company_id = @c AND r.code = @code
            ORDER BY v.version DESC
            """,
            r => new Version(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Date(3), r.GetBoolean(4)),
            cancellationToken,
            ("c", companyId),
            ("code", code)).ConfigureAwait(false);
    }

    private async Task<T?> ScalarAsync<T>(Guid companyId, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @company, true)", cancellationToken, ("company", companyId.ToString())).ConfigureAwait(false);
        await using var command = Sql.Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }
}
