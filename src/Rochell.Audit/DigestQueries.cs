using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Audit;

/// <summary>E-UI01-3: the daily digests written to WORM storage, newest first (audit:read).</summary>
public sealed record ListLedgerDigests(Guid CompanyId, Guid SessionId, int Limit = 50, int Offset = 0) : IQuery;

public sealed record LedgerDigestView(
    string Ledger, DateOnly DigestDate, long FirstSeq, long LastSeq, int ItemCount, string MerkleRoot, string DigestHash, string? PrevDigestHash, string WormObjectKey);

public sealed record LedgerDigestList(IReadOnlyList<LedgerDigestView> Items, int Limit, int Offset);

[RequiresPermission("audit:read")]
public sealed class ListLedgerDigestsHandler : IQueryHandler<ListLedgerDigests>
{
    public string QueryType => "Audit.ListLedgerDigests";

    public async Task<string> HandleAsync(ListLedgerDigests query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT ledger, digest_date, first_seq, last_seq, item_count, encode(merkle_root, 'hex'), encode(digest_hash, 'hex'), encode(prev_digest_hash, 'hex'), worm_object_key
            FROM audit.ledger_digest
            WHERE company_id = @c
            ORDER BY digest_date DESC, ledger
            LIMIT @limit OFFSET @offset
            """,
            r => new LedgerDigestView(r.GetString(0), r.Date(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.NullableString(7), r.GetString(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new LedgerDigestList(items, query.Limit, query.Offset));
    }
}
