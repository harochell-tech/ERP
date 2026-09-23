namespace Rochell.Migrations;

/// <summary>
/// A directory of forward-only migrations. "main" = db/migrations (production schema).
/// "test" = tests/migrations (test-only objects, never shipped by the CLI).
/// </summary>
public sealed record MigrationSource(string Name, string DirectoryPath)
{
    public const string Main = "main";
    public const string Test = "test";
}
