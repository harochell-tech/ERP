using Rochell.Migrations;

namespace Rochell.TestInfrastructure;

public static class TestPaths
{
    public static string MainMigrations => Path.Combine(AppContext.BaseDirectory, "migrations", MigrationSource.Main);

    public static string TestMigrations => Path.Combine(AppContext.BaseDirectory, "migrations", MigrationSource.Test);

    public static MigrationSource MainSource => new(MigrationSource.Main, MainMigrations);

    /// <summary>tests/migrations may be empty; the directory always exists in the output.</summary>
    public static MigrationSource TestSource
    {
        get
        {
            Directory.CreateDirectory(TestMigrations);
            return new(MigrationSource.Test, TestMigrations);
        }
    }

    public static IReadOnlyList<string> TestMigrationFiles
        => Directory.EnumerateFiles(TestSource.DirectoryPath, "*.sql").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> MainMigrationFiles
        => Directory.EnumerateFiles(MainMigrations, "*.sql").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
}
