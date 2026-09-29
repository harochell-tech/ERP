using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Rochell.MasterData.Rnc;

/// <summary>One taxpayer of the DGII registry (E-RNC-3).</summary>
public sealed record RncEntry(string Rnc, string LegalName, string? TradeName, string? Activity, DateOnly? StartedOn, string Status, string? Regime);

/// <summary>What an import did (E-RNC-1).</summary>
public sealed record RncImportResult(Guid ImportId, string FileName, DateOnly SourceDate, int Rows, int Skipped, string Sha256);

/// <summary>
/// E-RNC-1/2: the DGII's "Listado de todos los RNC" (DGII_RNC.zip with one TXT, or the TXT itself): Latin-1, CRLF, eleven fields
/// separated by '|' — RNC or cédula, legal name, trade name, activity, four unused, start date (dd/MM/yyyy), status, regime. A few
/// names carry line breaks, so a record is read until it has its eleven fields. Rows without a 9- or 11-digit number are skipped;
/// a repeated number keeps its last row. The import replaces the whole registry in one transaction (the deployment role runs it).
/// </summary>
public static class RncRegistryFile
{
    private const int Fields = 11;

    /// <summary>At most this many rows per INSERT (array parameters).</summary>
    private const int Batch = 10_000;

    public static (List<RncEntry> Entries, int Skipped) Parse(Stream text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StreamReader(text, Encoding.Latin1);
        var byRnc = new Dictionary<string, RncEntry>(StringComparer.Ordinal);
        var skipped = 0;
        var pending = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (pending.Length > 0)
            {
                pending.Append(' ');
            }

            pending.Append(line);
            var parts = pending.ToString().Split('|');
            if (parts.Length < Fields)
            {
                continue;
            }

            pending.Clear();
            var entry = parts.Length == Fields ? ToEntry(parts) : null;
            if (entry is null)
            {
                skipped++;
                continue;
            }

            if (byRnc.ContainsKey(entry.Rnc))
            {
                skipped++;
            }

            byRnc[entry.Rnc] = entry;
        }

        if (pending.Length > 0)
        {
            skipped++;
        }

        return ([.. byRnc.Values], skipped);
    }

    private static RncEntry? ToEntry(string[] parts)
    {
        static string? Clean(string value)
        {
            var trimmed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return trimmed.Length == 0 ? null : trimmed;
        }

        var rnc = parts[0].Trim();
        var legalName = Clean(parts[1]);
        var status = Clean(parts[9]);
        if ((rnc.Length != 9 && rnc.Length != 11) || !rnc.All(char.IsAsciiDigit) || legalName is null || status is null)
        {
            return null;
        }

        DateOnly? started = DateOnly.TryParseExact(parts[8].Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        return new RncEntry(rnc, legalName, Clean(parts[2]), Clean(parts[3]), started, status, Clean(parts[10]));
    }

    /// <summary>
    /// Reads <paramref name="path"/> (the ZIP or its TXT) and replaces md.rnc_registry. The source date is the ZIP entry's date (the
    /// DGII's), else the file's, unless given.
    /// </summary>
    public static async Task<RncImportResult> ImportAsync(DbConnection connection, string path, string importedBy, DateOnly? sourceDate, DateTime importedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var sha = SHA256.HashData(bytes);
        List<RncEntry> entries;
        int skipped;
        DateOnly date;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The ZIP has no TXT file.");
            await using var stream = entry.Open();
            (entries, skipped) = Parse(stream);
            date = sourceDate ?? DateOnly.FromDateTime(entry.LastWriteTime.DateTime);
        }
        else
        {
            (entries, skipped) = Parse(new MemoryStream(bytes));
            date = sourceDate ?? DateOnly.FromDateTime(File.GetLastWriteTime(path));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("The file has no valid registry rows; nothing was replaced.");
        }

        var importId = Guid.CreateVersion7();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, tx, "DELETE FROM md.rnc_registry", cancellationToken).ConfigureAwait(false);
        for (var start = 0; start < entries.Count; start += Batch)
        {
            var chunk = entries.Skip(start).Take(Batch).ToList();
            await ExecuteAsync(
                connection,
                tx,
                """
                INSERT INTO md.rnc_registry (rnc, legal_name, trade_name, activity, started_on, status, regime)
                SELECT * FROM unnest(@rnc::text[], @legal::text[], @trade::text[], @activity::text[], @started::date[], @status::text[], @regime::text[])
                """,
                cancellationToken,
                ("rnc", chunk.Select(e => e.Rnc).ToArray()),
                ("legal", chunk.Select(e => e.LegalName).ToArray()),
                ("trade", chunk.Select(e => e.TradeName).ToArray()),
                ("activity", chunk.Select(e => e.Activity).ToArray()),
                ("started", chunk.Select(e => e.StartedOn).ToArray()),
                ("status", chunk.Select(e => e.Status).ToArray()),
                ("regime", chunk.Select(e => e.Regime).ToArray())).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            tx,
            """
            INSERT INTO md.rnc_registry_import (import_id, file_name, file_sha256, source_date, row_count, skipped, imported_by, imported_at)
            VALUES (@id, @file, @sha, @date, @rows, @skipped, @by, @at)
            """,
            cancellationToken,
            ("id", importId),
            ("file", Path.GetFileName(path)),
            ("sha", sha),
            ("date", date),
            ("rows", entries.Count),
            ("skipped", skipped),
            ("by", importedBy),
            ("at", importedAt)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RncImportResult(importId, Path.GetFileName(path), date, entries.Count, skipped, Convert.ToHexStringLower(sha));
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.CommandTimeout = 600;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
