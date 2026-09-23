namespace Rochell.Migrations;

/// <summary>A migration file loaded from disk. <see cref="Sql"/> is LF-normalized; <see cref="Checksum"/> is SHA-256 of its UTF-8 bytes.</summary>
public sealed record MigrationScript(string Source, int Version, string Name, string FileName, string Sql, byte[] Checksum)
{
    public string ChecksumHex => Convert.ToHexString(Checksum);
}

/// <summary>A row of migrations.applied_migration.</summary>
public sealed record AppliedMigration(string Source, int Version, string Name, byte[] Checksum);

/// <summary>Result of a run: what was already applied, what this run applied, what remains pending (verify/status only).</summary>
public sealed record MigrationResult(
    string Source,
    int AlreadyApplied,
    IReadOnlyList<string> AppliedNow,
    IReadOnlyList<string> Pending);
