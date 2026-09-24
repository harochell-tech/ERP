using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// ADR-001 + E-PR08-1: module dependencies are an explicit allow-list (acyclic). Every module may use Rochell.Platform;
/// Procurement may also use Finance, Inventory, MasterData and Tax; Audit may also use Finance and Inventory, only to recompute their
/// row hashes (E-PR15-7). Platform depends on no module.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Dictionary<string, string[]> AllowedModuleDependencies = new(StringComparer.Ordinal)
    {
        ["Procurement"] = ["Finance", "Inventory", "MasterData", "Tax"],
        ["Audit"] = ["Finance", "Inventory"],
    };

    public static TheoryData<string> Modules() => new(Repo.Modules);

    private static string[] Allowed(string module) => AllowedModuleDependencies.GetValueOrDefault(module) ?? [];

    [Fact]
    public void Allowed_module_dependencies_are_acyclic_and_refer_to_real_modules()
    {
        foreach (var (module, dependencies) in AllowedModuleDependencies)
        {
            Assert.Contains(module, Repo.Modules);
            Assert.All(dependencies, d => Assert.Contains(d, Repo.Modules));
        }

        foreach (var module in Repo.Modules)
        {
            var stack = new Stack<(string Node, string Path)>([(module, module)]);
            while (stack.Count > 0)
            {
                var (node, path) = stack.Pop();
                foreach (var next in Allowed(node))
                {
                    Assert.True(next != module, $"Dependency cycle: {path} -> {next}");
                    stack.Push((next, $"{path} -> {next}"));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_assembly_does_not_depend_on_other_modules(string module)
    {
        var forbidden = Repo.Modules.Where(m => m != module && !Allowed(module).Contains(m)).Select(m => $"Rochell.{m}")
            .Append("Rochell.Migrations").Append("Rochell.Api").ToArray();

        var result = Types.InAssembly(Assembly.Load($"Rochell.{module}")).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Rochell.{module} depends on a module outside its allow-list: {string.Join(", ", result.FailingTypeNames ?? [])}");
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
    public void Module_project_references_stay_within_the_allow_list(string module)
    {
        var csproj = XDocument.Load(Path.Combine(Repo.Src, $"Rochell.{module}", $"Rochell.{module}.csproj"));

        var references = csproj.Descendants("ProjectReference")
            // csproj paths use '\\' on every OS; normalize so Path.GetFileName works on Linux CI too.
            .Select(r => Path.GetFileName(((string?)r.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
            .ToList();
        var permitted = Allowed(module).Select(m => $"Rochell.{m}.csproj").Append("Rochell.Platform.csproj").ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Rochell.Platform.csproj", references);
        Assert.True(references.All(permitted.Contains), $"Rochell.{module} references outside its allow-list: {string.Join(", ", references.Where(r => !permitted.Contains(r)))}");
    }
}
