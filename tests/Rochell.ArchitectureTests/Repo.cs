using System.Reflection;

namespace Rochell.ArchitectureTests;

internal static class Repo
{
    public static readonly string[] Modules =
        ["Identity", "MasterData", "Finance", "Inventory", "Procurement", "Tax", "Audit", "Reconciliation"];

    /// <summary>Every production assembly under src/. Loaded by name so internal Program types are included.</summary>
    public static readonly string[] ProductionAssemblyNames =
        ["Rochell.Platform", .. Modules.Select(m => $"Rochell.{m}"), "Rochell.Migrations", "rochell-migrate", "Rochell.Api"];

    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    public static IEnumerable<Assembly> ProductionAssemblies => ProductionAssemblyNames.Select(Assembly.Load);

    public static IEnumerable<string> Files(string relativeDirectory, string pattern)
        => Directory.EnumerateFiles(Path.Combine(Root, relativeDirectory), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rochell.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (Rochell.slnx) not found.");
    }
}
