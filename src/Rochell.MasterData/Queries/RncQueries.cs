using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.MasterData.Queries;

// E-RNC-4/5/7: the DGII registry, read with rnc:read by the roles that create or review customers and suppliers. It only helps: the
// screens propose the legal name and warn about the status; the commands keep what the user confirms (E-RNC-6).

public sealed record GetRnc(Guid CompanyId, Guid SessionId, string Rnc) : IQuery;

/// <summary><c>Found</c> false when the number is not in the registry (or no registry was imported: <c>RegistryDate</c> null).</summary>
public sealed record RncLookup(
    string Rnc, bool Found, string? LegalName, string? TradeName, string? Activity, DateOnly? StartedOn, string? Status, string? Regime, DateOnly? RegistryDate);

[RequiresPermission("rnc:read")]
public sealed class GetRncHandler : IQueryHandler<GetRnc>
{
    public string QueryType => "MasterData.GetRnc";

    private sealed record Row(string LegalName, string? TradeName, string? Activity, DateOnly? StartedOn, string Status, string? Regime);

    public async Task<string> HandleAsync(GetRnc query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var rnc = new string((query.Rnc ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (rnc.Length is not (9 or 11))
        {
            throw new DomainException(QueryErrors.InvalidParameter, "An RNC has 9 digits and a cédula 11.");
        }

        var registryDate = await RncRegistry.DateAsync(context, cancellationToken).ConfigureAwait(false);
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT legal_name, trade_name, activity, started_on, status, regime FROM md.rnc_registry WHERE rnc = @r",
            r => new Row(r.GetString(0), r.NullableString(1), r.NullableString(2), r.IsDBNull(3) ? null : r.Date(3), r.GetString(4), r.NullableString(5)),
            cancellationToken,
            ("r", rnc)).ConfigureAwait(false);
        return ApiJson.Serialize(row is null
            ? new RncLookup(rnc, false, null, null, null, null, null, null, registryDate)
            : new RncLookup(rnc, true, row.LegalName, row.TradeName, row.Activity, row.StartedOn, row.Status, row.Regime, registryDate));
    }
}

public sealed record GetRncRegistryStatus(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record RncRegistryImportView(DateOnly SourceDate, string FileName, int Rows, int Skipped, string ImportedBy, DateTime ImportedAt);

/// <summary><c>Issue</c>: NOT_FOUND, NOT_ACTIVE (with the registry's status) or NAME_DIFFERS.</summary>
public sealed record RncDiscrepancy(Guid PartyId, string Rnc, string LegalName, bool IsCustomer, bool IsSupplier, string Issue, string? RegistryName, string? RegistryStatus);

public sealed record RncRegistryStatus(RncRegistryImportView? LastImport, IReadOnlyList<RncDiscrepancy> Discrepancies);

/// <summary>E-RNC-7: the registry in force and the company's customers and suppliers whose RNC is missing, not ACTIVO, or named otherwise.</summary>
[RequiresPermission("rnc:read")]
public sealed class GetRncRegistryStatusHandler : IQueryHandler<GetRncRegistryStatus>
{
    public string QueryType => "MasterData.GetRncRegistryStatus";

    public async Task<string> HandleAsync(GetRncRegistryStatus query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var last = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT source_date, file_name, row_count, skipped, imported_by, imported_at FROM md.rnc_registry_import ORDER BY imported_at DESC LIMIT 1",
            r => new RncRegistryImportView(r.Date(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetFieldValue<DateTime>(5)),
            cancellationToken).ConfigureAwait(false);
        var discrepancies = last is null
            ? []
            : await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT p.party_id, p.rnc, p.legal_name, p.is_customer, p.is_supplier,
                       CASE WHEN g.rnc IS NULL THEN 'NOT_FOUND' WHEN g.status <> 'ACTIVO' THEN 'NOT_ACTIVE' ELSE 'NAME_DIFFERS' END,
                       g.legal_name, g.status
                FROM md.party p
                LEFT JOIN md.rnc_registry g ON g.rnc = p.rnc
                WHERE p.company_id = @c AND p.rnc IS NOT NULL AND (p.is_customer OR p.is_supplier)
                  AND (g.rnc IS NULL OR g.status <> 'ACTIVO'
                       OR regexp_replace(upper(g.legal_name), '[^A-Z0-9]', '', 'g') <> regexp_replace(upper(p.legal_name), '[^A-Z0-9]', '', 'g'))
                ORDER BY p.legal_name
                """,
                r => new RncDiscrepancy(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetString(5), r.NullableString(6), r.NullableString(7)),
                cancellationToken,
                ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new RncRegistryStatus(last, discrepancies));
    }
}

internal static class RncRegistry
{
    public static async Task<DateOnly?> DateAsync(QueryContext context, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
               context.Connection,
               context.Transaction,
               "SELECT source_date FROM md.rnc_registry_import ORDER BY imported_at DESC LIMIT 1",
               r => r.Date(0),
               cancellationToken).ConfigureAwait(false))
           .Select(d => (DateOnly?)d).FirstOrDefault();
}
