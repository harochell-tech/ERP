using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

public sealed partial class RepositoryConventionTests
{
    [Fact]
    public void Package_versions_are_centrally_managed()
    {
        var offenders = Repo.Files(".", "*.csproj").Where(f => PackageReferenceWithVersion().IsMatch(File.ReadAllText(f))).ToList();

        Assert.True(offenders.Count == 0, "Versions belong in Directory.Packages.props:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void No_project_weakens_compiler_guardrails()
    {
        var offenders = Repo.Files(".", "*.csproj")
            .Concat(Repo.Files(".", "Directory.Build.props"))
            .Where(f => WeakenedGuardrail().IsMatch(File.ReadAllText(f)))
            .ToList();

        Assert.True(offenders.Count == 0, "TreatWarningsAsErrors/Nullable disabled or RS0030 suppressed in:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Src_projects_use_banned_api_analyzer()
    {
        var props = File.ReadAllText(Path.Combine(Repo.Src, "Directory.Build.props"));

        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", props, StringComparison.Ordinal);
        Assert.Contains("BannedSymbols.txt", props, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_files_are_forced_to_lf()
    {
        var attributes = File.ReadAllText(Path.Combine(Repo.Root, ".gitattributes"));

        Assert.Contains("*.sql text eol=lf", attributes, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_files_follow_forward_only_conventions()
    {
        foreach (var directory in new[] { Path.Combine("db", "migrations"), Path.Combine("tests", "migrations") })
        {
            var path = Path.Combine(Repo.Root, directory);
            var scripts = Rochell.Migrations.MigrationDiscovery.Discover(new Rochell.Migrations.MigrationSource(directory, path));
            Assert.All(Directory.EnumerateFiles(path, "*.sql"), f => Assert.DoesNotContain('\r', File.ReadAllText(f)));
            Assert.All(scripts, s => Assert.DoesNotContain("down", s.Name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Only_supported_environments_have_settings_files()
    {
        var allowed = new[] { "appsettings.json", "appsettings.Development.json", "appsettings.Test.json", "appsettings.Staging.json" };

        var unexpected = Repo.Files("src", "appsettings*.json").Select(Path.GetFileName).Where(n => !allowed.Contains(n)).ToList();

        Assert.True(unexpected.Count == 0, "Unexpected settings files: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void Test_and_staging_settings_contain_no_credentials()
    {
        var offenders = Repo.Files("src", "appsettings.Test.json").Concat(Repo.Files("src", "appsettings.Staging.json"))
            .Where(f => File.ReadAllText(f).Contains("password", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0, "Credentials must come from environment variables in Test/Staging:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Migrations_cli_ships_only_production_migrations()
    {
        var csproj = File.ReadAllText(Path.Combine(Repo.Src, "Rochell.Migrations.Cli", "Rochell.Migrations.Cli.csproj"));

        Assert.Contains(@"..\..\db\migrations\*.sql", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain(@"tests\migrations", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"..\migrations", csproj, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<PackageReference[^>]*\bVersion\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReferenceWithVersion();

    [GeneratedRegex(@"<TreatWarningsAsErrors>\s*false|<Nullable>\s*disable|RS0030", RegexOptions.IgnoreCase)]
    private static partial Regex WeakenedGuardrail();
}
