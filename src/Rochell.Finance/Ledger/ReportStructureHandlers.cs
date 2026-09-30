using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Finance.Ledger;

internal static class ReportStructures
{
    public const string Aggregate = "ReportStructure";

    /// <summary>The account classes each report presents.</summary>
    public static IReadOnlyList<string> ClassesOf(string report) => report switch
    {
        "BALANCE_SHEET" => ["ASSET", "LIABILITY", "EQUITY"],
        "INCOME_STATEMENT" => ["REVENUE", "COST", "EXPENSE"],
        _ => throw new DomainException(LedgerErrors.StructureInvalid, "The report must be BALANCE_SHEET or INCOME_STATEMENT."),
    };
}

[RequiresPermission("account:manage")]
public sealed class PrepareReportStructureHandler : ICommandHandler<PrepareReportStructure>
{
    public string CommandType => "Finance.PrepareReportStructure";

    public async Task<string> HandleAsync(PrepareReportStructure command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var report = (command.Report ?? string.Empty).Trim().ToUpperInvariant();
        var classes = ReportStructures.ClassesOf(report);
        var lines = command.Lines ?? [];
        if (lines.Count == 0)
        {
            throw new DomainException(LedgerErrors.StructureInvalid, "A report structure has at least one line.");
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var code = (line.LineCode ?? string.Empty).Trim();
            if (code.Length is 0 or > 20 || !codes.Add(code) || string.IsNullOrWhiteSpace(line.Caption) || line.Caption.Trim().Length > 200 || line.Sign is not (1 or -1))
            {
                throw new DomainException(LedgerErrors.StructureInvalid, "Each line has a unique code (1–20 characters), a caption (1–200 characters) and sign 1 or -1.");
            }
        }

        var parents = lines.ToDictionary(l => l.LineCode.Trim(), l => l.ParentLineCode?.Trim(), StringComparer.Ordinal);
        foreach (var code in parents.Keys)
        {
            // Every parent exists and no line is its own ancestor.
            var seen = new HashSet<string>(StringComparer.Ordinal) { code };
            for (var parent = parents[code]; parent is not null; parent = parents[parent])
            {
                if (!parents.ContainsKey(parent) || !seen.Add(parent))
                {
                    throw new DomainException(LedgerErrors.StructureInvalid, $"Line {code}: its parent {parent} does not exist or forms a cycle.");
                }
            }
        }

        var accounts = lines.SelectMany(l => l.AccountIds ?? []).ToList();
        if (accounts.Count != accounts.Distinct().Count())
        {
            throw new DomainException(LedgerErrors.StructureInvalid, "Each account is on one line only (E-FIN1-5).");
        }

        var wrong = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce(a.code, x.id::text) FROM unnest(@ids) AS x (id) LEFT JOIN fin.account a ON a.account_id = x.id AND a.company_id = @c
            WHERE a.account_id IS NULL OR a.account_class IS NULL OR NOT (a.account_class = ANY (@classes)) ORDER BY 1
            """,
            r => r.GetString(0),
            cancellationToken,
            ("ids", accounts.ToArray()),
            ("c", context.CompanyId),
            ("classes", classes.ToArray())).ConfigureAwait(false);
        if (wrong.Count > 0)
        {
            throw new DomainException(LedgerErrors.StructureInvalid, $"Accounts that do not belong to {report} ({string.Join("/", classes)}): {string.Join(", ", wrong)}.");
        }

        var preparer = await LedgerSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended('report-structure:' || @c || ':' || @r, 0))", cancellationToken, ("c", context.CompanyId.ToString()), ("r", report)).ConfigureAwait(false);
        var version = (await LedgerSql.ScalarAsync<int?>(
            context, "SELECT max(version) FROM fin.report_structure_version WHERE company_id = @c AND report = @r", cancellationToken, ("c", context.CompanyId), ("r", report)).ConfigureAwait(false) ?? 0) + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReportStructurePrepared",
                1,
                ReportStructures.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { structureVersionId = context.ResultRef, report, version, effectiveFrom = command.EffectiveFrom, lines = lines.Count, accounts = accounts.Count }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO fin.report_structure_version (structure_version_id, company_id, report, version, effective_from, status, prepared_by) VALUES (@id, @c, @r, @v, @from, 'DRAFT', @by)",
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("r", report),
            ("v", version),
            ("from", command.EffectiveFrom),
            ("by", preparer)).ConfigureAwait(false);

        // Parents first, so every parent_line_code already exists.
        var ordered = new List<ReportLineInput>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (ordered.Count < lines.Count)
        {
            foreach (var line in lines.Where(l => !placed.Contains(l.LineCode.Trim()) && (l.ParentLineCode is null || placed.Contains(l.ParentLineCode.Trim()))).ToList())
            {
                ordered.Add(line);
                placed.Add(line.LineCode.Trim());
            }
        }

        foreach (var line in ordered)
        {
            var lineId = context.Ids.NewId();
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO fin.report_line (report_line_id, company_id, structure_version_id, line_code, caption, parent_line_code, sign, order_no) VALUES (@id, @c, @s, @code, @caption, @parent, @sign, @order)",
                cancellationToken,
                ("id", lineId),
                ("c", context.CompanyId),
                ("s", context.ResultRef),
                ("code", line.LineCode.Trim()),
                ("caption", line.Caption.Trim()),
                ("parent", line.ParentLineCode?.Trim()),
                ("sign", line.Sign),
                ("order", line.OrderNo)).ConfigureAwait(false);
            foreach (var account in line.AccountIds ?? [])
            {
                await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    "INSERT INTO fin.report_line_account (company_id, structure_version_id, report_line_id, account_id) VALUES (@c, @s, @l, @a)",
                    cancellationToken,
                    ("c", context.CompanyId),
                    ("s", context.ResultRef),
                    ("l", lineId),
                    ("a", account)).ConfigureAwait(false);
            }
        }

        await context.AppendStateAsync(ReportStructures.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { structureVersionId = context.ResultRef, report, version, status = "DRAFT" });
    }
}

[RequiresPermission("report_structure:approve", StepUp = true)]
public sealed class ApproveReportStructureHandler : ICommandHandler<ApproveReportStructure>
{
    public string CommandType => "Finance.ApproveReportStructure";

    private sealed record Row(string Report, int Version, string Status, Guid PreparedBy);

    public async Task<string> HandleAsync(ApproveReportStructure command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT report, version, status, prepared_by FROM fin.report_structure_version WHERE company_id = @c AND structure_version_id = @id FOR UPDATE",
            r => new Row(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", command.StructureVersionId)).ConfigureAwait(false)
            ?? throw new DomainException(LedgerErrors.NotFound, "The report structure does not exist.");
        if (row.Status != "DRAFT")
        {
            throw new DomainException(LedgerErrors.InvalidState, $"The report structure is {row.Status}.");
        }

        var approver = await LedgerSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(LedgerErrors.FourEyes, "A report structure is approved by someone other than who prepared it (E-FIN1-5).");
        }

        // E-FIN1-03-2: every active account of the report's classes is on a line.
        var missing = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.code FROM fin.account a
            WHERE a.company_id = @c AND a.status = 'ACTIVE' AND a.account_class = ANY (@classes)
              AND NOT EXISTS (SELECT 1 FROM fin.report_line_account x WHERE x.structure_version_id = @id AND x.account_id = a.account_id)
            ORDER BY a.code
            """,
            r => r.GetString(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("classes", ReportStructures.ClassesOf(row.Report).ToArray()),
            ("id", command.StructureVersionId)).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new DomainException(LedgerErrors.StructureIncomplete, $"Active accounts without a line: {string.Join(", ", missing)}.");
        }

        var previous = await LedgerSql.ScalarAsync<Guid?>(
            context, "SELECT structure_version_id FROM fin.report_structure_version WHERE company_id = @c AND report = @r AND status = 'ACTIVE' FOR UPDATE", cancellationToken, ("c", context.CompanyId), ("r", row.Report)).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReportStructureApproved",
                1,
                ReportStructures.Aggregate,
                command.StructureVersionId,
                2,
                JsonSerializer.Serialize(new { structureVersionId = command.StructureVersionId, report = row.Report, version = row.Version, supersedes = previous }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (previous is { } old)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE fin.report_structure_version SET status = 'SUPERSEDED' WHERE structure_version_id = @id", cancellationToken, ("id", old)).ConfigureAwait(false);
            await context.AppendStateAsync(ReportStructures.Aggregate, old, "DOCUMENT", "ACTIVE", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.report_structure_version SET status = 'ACTIVE', approved_by = @by WHERE structure_version_id = @id",
            cancellationToken,
            ("by", approver),
            ("id", command.StructureVersionId)).ConfigureAwait(false);
        await context.AppendStateAsync(ReportStructures.Aggregate, command.StructureVersionId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { structureVersionId = command.StructureVersionId, report = row.Report, version = row.Version, status = "ACTIVE", superseded = previous });
    }
}
