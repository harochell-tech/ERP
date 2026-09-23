namespace Rochell.Migrations;

/// <summary>
/// Compares files on disk with the journal. Forward-only rules:
/// an applied migration can never be modified or deleted, and new migrations must come after the last applied one.
/// </summary>
public static class MigrationPlanner
{
    public static IReadOnlyList<MigrationScript> PendingScripts(
        IReadOnlyList<MigrationScript> scripts,
        IReadOnlyList<AppliedMigration> applied)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(applied);

        var byVersion = scripts.ToDictionary(s => s.Version);
        foreach (var row in applied)
        {
            if (!byVersion.TryGetValue(row.Version, out var script))
            {
                throw new MigrationException(
                    $"Applied migration {row.Version:D4} ({row.Name}) of source '{row.Source}' no longer exists on disk. Migrations are forward-only and must never be deleted.");
            }

            if (!script.Checksum.AsSpan().SequenceEqual(row.Checksum))
            {
                throw new MigrationException(
                    $"Migration '{script.FileName}' of source '{row.Source}' was modified after being applied (checksum mismatch). Create a new migration instead of editing an applied one.");
            }
        }

        var appliedVersions = applied.Select(a => a.Version).ToHashSet();
        var maxApplied = applied.Count == 0 ? 0 : applied.Max(a => a.Version);
        var pending = scripts.Where(s => !appliedVersions.Contains(s.Version)).ToList();

        var outOfOrder = pending.Where(s => s.Version < maxApplied).Select(s => s.FileName).ToList();
        if (outOfOrder.Count > 0)
        {
            throw new MigrationException(
                $"Out-of-order migrations in source '{scripts[0].Source}' (older than the last applied version {maxApplied:D4}): {string.Join(", ", outOfOrder)}.");
        }

        return pending;
    }
}
