using System.Data.Common;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;

namespace Rochell.Audit;

/// <summary>A group's rows as they are now: the hash each row stored at insert and the hash recomputed from its columns.</summary>
public sealed record GroupRows(IReadOnlyList<(byte[] Stored, byte[] Recomputed)> Rows)
{
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Position of the first row whose data no longer matches the hash it stored, or null.</summary>
    public int? FirstAlteredRow
    {
        get
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                if (!Rows[i].Stored.AsSpan().SequenceEqual(Rows[i].Recomputed))
                {
                    return i;
                }
            }

            return null;
        }
    }

    /// <summary>Group hash from the recomputed row hashes: sealing and verification never trust stored hashes.</summary>
    public byte[] Hash() => ChainHash.Group(Rows.Select(r => r.Recomputed));
}

/// <summary>Reads a group from the database and recomputes each row hash with the row definitions that wrote it.</summary>
public static class GroupReader
{
    public static Task<GroupRows> ReadAsync(DbConnection connection, DbTransaction? transaction, Guid companyId, string ledger, Guid groupRef, CancellationToken cancellationToken)
        => ledger switch
        {
            Chains.Gl => ReadGlAsync(connection, transaction, companyId, groupRef, cancellationToken),
            Chains.InventoryQuantity => ReadAsync(
                connection,
                transaction,
                """
                SELECT quantity_entry_id, company_id, movement_type::text, plant_id, location_id, item_id, lot_id, quantity, source_event_id,
                       source_document_type, source_document_id, source_line_id, reverses_quantity_entry_id, occurred_at, recorded_at,
                       business_date, posting_date, row_hash
                FROM inv.inv_quantity_entry WHERE company_id = @c AND source_event_id = @g ORDER BY quantity_entry_id
                """,
                companyId,
                groupRef,
                r => new QuantityEntryRow(
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), r.GetGuid(6), r.GetDecimal(7), r.GetGuid(8),
                    r.GetString(9), r.GetGuid(10), NullableGuid(r, 11), NullableGuid(r, 12), r.GetDateTime(13), r.GetDateTime(14),
                    r.GetFieldValue<DateOnly>(15), r.GetFieldValue<DateOnly>(16)).ComputeRowHash(),
                17,
                cancellationToken),
            Chains.InventoryValue => ReadAsync(
                connection,
                transaction,
                """
                SELECT value_entry_id, company_id, movement_type::text, valuation_area_id, plant_id, item_id, quantity_entry_id, amount,
                       source_event_id, reverses_value_entry_id, occurred_at, recorded_at, business_date, posting_date, row_hash
                FROM inv.inv_value_entry WHERE company_id = @c AND source_event_id = @g ORDER BY value_entry_id
                """,
                companyId,
                groupRef,
                r => new ValueEntryRow(
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), NullableGuid(r, 6), r.GetDecimal(7),
                    r.GetGuid(8), NullableGuid(r, 9), r.GetDateTime(10), r.GetDateTime(11), r.GetFieldValue<DateOnly>(12),
                    r.GetFieldValue<DateOnly>(13)).ComputeRowHash(),
                14,
                cancellationToken),
            Chains.DomainEvent => ReadAsync(
                connection,
                transaction,
                """
                SELECT event_id, company_id, command_id, command_event_index, event_type, schema_version, aggregate_type, aggregate_id,
                       aggregate_version, event_sequence, occurred_at, recorded_at, business_date, session_id, correlation_id, causation_id,
                       payload::text, row_hash
                FROM core.domain_event WHERE company_id = @c AND command_id = @g ORDER BY command_event_index
                """,
                companyId,
                groupRef,
                r => new DomainEventRow(
                    r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetGuid(7),
                    r.GetInt64(8), r.GetInt16(9), r.GetDateTime(10), r.GetDateTime(11), r.GetFieldValue<DateOnly>(12), r.GetGuid(13),
                    r.GetGuid(14), NullableGuid(r, 15), r.GetString(16)).ComputeRowHash(),
                17,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(ledger), ledger, "Unknown chain."),
        };

    private static async Task<GroupRows> ReadGlAsync(DbConnection connection, DbTransaction? transaction, Guid companyId, Guid journalId, CancellationToken cancellationToken)
    {
        var header = await ReadAsync(
            connection,
            transaction,
            """
            SELECT journal_id, company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version, posting_generation,
                   journal_type, reverses_journal_id, late_entry, occurred_at, row_hash
            FROM fin.gl_journal WHERE company_id = @c AND journal_id = @g
            """,
            companyId,
            journalId,
            r => new GlJournalRow(
                r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), r.GetInt32(6), r.GetInt32(7),
                r.GetString(8), NullableGuid(r, 9), r.GetBoolean(10), r.GetDateTime(11)).ComputeRowHash(),
            12,
            cancellationToken).ConfigureAwait(false);
        if (header.IsEmpty)
        {
            return header;
        }

        var entries = await ReadAsync(
            connection,
            transaction,
            """
            SELECT gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, currency, plant_id,
                   item_id, party_id, subledger_type, subledger_ref, inv_value_entry_id, source_event_id, rule_line_code,
                   determination_inputs::text, row_hash
            FROM fin.gl_entry WHERE company_id = @c AND journal_id = @g ORDER BY line_no
            """,
            companyId,
            journalId,
            r => new GlEntryRow(
                r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetFieldValue<DateOnly>(4), r.GetGuid(5), r.GetString(6),
                r.GetDecimal(7), r.GetDecimal(8), r.GetString(9), NullableGuid(r, 10), NullableGuid(r, 11), NullableGuid(r, 12),
                r.IsDBNull(13) ? null : r.GetString(13), NullableGuid(r, 14), NullableGuid(r, 15), r.GetGuid(16), r.GetString(17),
                r.GetString(18)).ComputeRowHash(),
            19,
            cancellationToken).ConfigureAwait(false);
        return new GroupRows([.. header.Rows, .. entries.Rows]);
    }

    private static async Task<GroupRows> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        Guid companyId,
        Guid groupRef,
        Func<DbDataReader, byte[]> recompute,
        int storedHashOrdinal,
        CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction, sql, ("c", companyId), ("g", groupRef));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(byte[] Stored, byte[] Recomputed)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetFieldValue<byte[]>(storedHashOrdinal), recompute(reader)));
        }

        return new GroupRows(rows);
    }

    private static Guid? NullableGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
}
