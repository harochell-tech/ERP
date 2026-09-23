namespace Rochell.Migrations.Tests.Infrastructure;

internal static class TestPaths
{
    public static string MainMigrations => Path.Combine(AppContext.BaseDirectory, "migrations", MigrationSource.Main);

    public static MigrationSource MainSource => new(MigrationSource.Main, MainMigrations);
}

/// <summary>Temporary copy of the main migrations that a test can edit, delete or extend.</summary>
internal sealed class ScratchMigrations : IDisposable
{
    public ScratchMigrations()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "rochell-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        foreach (var file in Directory.EnumerateFiles(TestPaths.MainMigrations, "*.sql"))
        {
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)));
        }
    }

    public string DirectoryPath { get; }

    public MigrationSource Source => new(MigrationSource.Main, DirectoryPath);

    public void Write(string fileName, string sql) => File.WriteAllText(Path.Combine(DirectoryPath, fileName), sql);

    public void Append(string fileName, string text) => File.AppendAllText(Path.Combine(DirectoryPath, fileName), text);

    public void Delete(string fileName) => File.Delete(Path.Combine(DirectoryPath, fileName));

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
