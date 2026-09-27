using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Tax;

// E-B03-15-1: fiscal sources and rules (with every version, its linked sources and its latest test run) for the fiscal screens.

public sealed record ListFiscalSources(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record FiscalSourceView(
    Guid SourceId,
    string OfficialSource,
    string DocumentTitle,
    string DocumentVersion,
    DateOnly PublicationDate,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string UrlOrReference,
    string FileSha256,
    string Environment);

public sealed record FiscalSourceList(IReadOnlyList<FiscalSourceView> Items);

[RequiresPermission("configuration:read")]
public sealed class ListFiscalSourcesHandler : IQueryHandler<ListFiscalSources>
{
    public string QueryType => "Tax.ListFiscalSources";

    public async Task<string> HandleAsync(ListFiscalSources query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT source_id, official_source, document_title, document_version, publication_date, effective_from, effective_to,
                   url_or_reference, encode(file_hash, 'hex'), environment
            FROM tax.fiscal_rule_source
            WHERE company_id = @c
            ORDER BY effective_from DESC, document_title
            """,
            r => new FiscalSourceView(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Date(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6),
                r.GetString(7), r.GetString(8), r.GetString(9)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new FiscalSourceList(items));
    }
}

public sealed record ListFiscalRules(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record FiscalTestRunView(bool Passed, int Cases, string Environment, DateTime ExecutedAt);

public sealed record LinkedSourceView(Guid SourceId, string DocumentTitle, string Environment);

public sealed record FiscalRuleVersionView(
    Guid RuleVersionId,
    int Version,
    string Status,
    string Definition,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? ConfiguredBy,
    string? ActivatedBy,
    long RowVersion,
    IReadOnlyList<LinkedSourceView> Sources,
    FiscalTestRunView? LatestTestRun);

public sealed record FiscalRuleView(Guid RuleId, string Code, string RuleKind, IReadOnlyList<FiscalRuleVersionView> Versions);

public sealed record FiscalRuleList(IReadOnlyList<FiscalRuleView> Items);

[RequiresPermission("configuration:read")]
public sealed class ListFiscalRulesHandler : IQueryHandler<ListFiscalRules>
{
    public string QueryType => "Tax.ListFiscalRules";

    public async Task<string> HandleAsync(ListFiscalRules query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rules = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT rule_id, code, rule_kind FROM tax.fiscal_rule WHERE company_id = @c ORDER BY code",
            r => (Id: r.GetGuid(0), Code: r.GetString(1), Kind: r.GetString(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var sources = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.rule_version_id, s.source_id, s.document_title, s.environment
            FROM tax.fiscal_rule_version_source l JOIN tax.fiscal_rule_source s ON s.source_id = l.source_id
            WHERE l.company_id = @c
            ORDER BY l.linked_at
            """,
            r => (Version: r.GetGuid(0), View: new LinkedSourceView(r.GetGuid(1), r.GetString(2), r.GetString(3))),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).ToLookup(s => s.Version, s => s.View);
        var runs = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT DISTINCT ON (rule_version_id) rule_version_id, passed, cases, environment, executed_at
            FROM tax.fiscal_rule_test_run
            WHERE company_id = @c
            ORDER BY rule_version_id, executed_at DESC
            """,
            r => (Version: r.GetGuid(0), View: new FiscalTestRunView(r.GetBoolean(1), r.GetInt32(2), r.GetString(3), r.Utc(4))),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).ToDictionary(r => r.Version, r => r.View);
        var versions = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.rule_id, v.rule_version_id, v.version, v.status, v.definition::text, v.effective_from, v.effective_to, c.email, a.email, v.row_version
            FROM tax.fiscal_rule_version v
            JOIN iam.user c ON c.user_id = v.configured_by
            LEFT JOIN iam.user a ON a.user_id = v.activated_by
            WHERE v.company_id = @c
            ORDER BY v.rule_id, v.version DESC
            """,
            r => (Rule: r.GetGuid(0), View: new FiscalRuleVersionView(
                r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6),
                r.NullableString(7), r.NullableString(8), r.GetInt64(9), [], null)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false))
            .Select(v => (v.Rule, View: v.View with
            {
                Sources = sources[v.View.RuleVersionId].ToList(),
                LatestTestRun = runs.GetValueOrDefault(v.View.RuleVersionId),
            }))
            .ToLookup(v => v.Rule, v => v.View);

        return ApiJson.Serialize(new FiscalRuleList(rules.Select(r => new FiscalRuleView(r.Id, r.Code, r.Kind, versions[r.Id].ToList())).ToList()));
    }
}
