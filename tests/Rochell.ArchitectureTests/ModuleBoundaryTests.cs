using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>ADR-001: modules depend only on Rochell.Platform. Platform depends on no module.</summary>
public sealed class ModuleBoundaryTests
{
    public static TheoryData<string> Modules() => new(Repo.Modules);

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_assembly_does_not_depend_on_other_modules(string module)
    {
        var forbidden = Repo.Modules.Where(m => m != module).Select(m => $"Rochell.{m}")
            .Append("Rochell.Migrations").Append("Rochell.Api").ToArray();

        var result = Types.InAssembly(Assembly.Load($"Rochell.{module}")).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Rochell.{module} depends on another module: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Platform_depends_on_no_module()
    {
        var forbidden = Repo.Modules.Select(m => $"Rochell.{m}").Append("Rochell.Migrations").Append("Rochell.Api").ToArray();

        var result = Types.InAssembly(Assembly.Load("Rochell.Platform")).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Rochell.Platform depends on a module: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Platform_stays_provider_agnostic()
    {
        var csproj = XDocument.Load(Path.Combine(Repo.Src, "Rochell.Platform", "Rochell.Platform.csproj"));

        Assert.Empty(csproj.Descendants("PackageReference"));
        Assert.Empty(csproj.Descendants("ProjectReference"));
    }

    [Fact]
    public void Test_only_commands_do_not_exist_in_production_assemblies()
    {
        var offenders = Repo.ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.StartsWith("Ping", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0, "Test-only types found in production: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_project_references_only_platform(string module)
    {
        var csproj = XDocument.Load(Path.Combine(Repo.Src, $"Rochell.{module}", $"Rochell.{module}.csproj"));

        var references = csproj.Descendants("ProjectReference")
            // csproj paths use '\' on every OS; normalize so Path.GetFileName works on Linux CI too.
            .Select(r => Path.GetFileName(((string?)r.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
            .ToList();

        Assert.Equal(["Rochell.Platform.csproj"], references);
    }
}
