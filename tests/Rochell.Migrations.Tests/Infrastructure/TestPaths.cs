using System.Globalization;
using Rochell.TestInfrastructure;

namespace Rochell.Migrations.Tests.Infrastructure;

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

    public Rochell.Migrations.MigrationSource Source => new(Rochell.Migrations.MigrationSource.Main, DirectoryPath);

    /// <summary>Number of the first version after the real main migrations.</summary>
    public static int NextVersion => TestPaths.MainMigrationFiles.Count + 1;

    /// <summary>File name for the version <paramref name="offset"/> positions after the last real migration (0 = next).</summary>
    public static string NextFile(string name, int offset = 0)
        => $"{(NextVersion + offset).ToString("D4", CultureInfo.InvariantCulture)}__{name}.sql";

    public static string LastRealFile => TestPaths.MainMigrationFiles[^1];

    public void Write(string fileName, string sql) => File.WriteAllText(Path.Combine(DirectoryPath, fileName), sql);

    public void Append(string fileName, string text) => File.AppendAllText(Path.Combine(DirectoryPath, fileName), text);

    public void Delete(string fileName) => File.Delete(Path.Combine(DirectoryPath, fileName));

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
