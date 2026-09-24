using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>Baseline §17 PR-03: no command without permission evaluation.</summary>
public sealed partial class CommandAuthorizationTests
{
    private static IEnumerable<Type> ProductionHandlers => Repo.ProductionAssemblies
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false }
                    && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<>)));

    [Fact]
    public void Every_production_command_handler_declares_a_permission()
    {
        var handlers = ProductionHandlers.ToList();
        var missing = handlers
            .Where(t => t.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: false).Length != 1)
            .Select(t => t.FullName)
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.True(missing.Count == 0, "Handlers without [RequiresPermission]: " + string.Join(", ", missing));
    }

    [Fact]
    public void Declared_permissions_exist_in_production_migrations()
    {
        var seeded = Directory.EnumerateFiles(Path.Combine(Repo.Root, "db", "migrations"), "*.sql")
            .SelectMany(f => SeededPermission().Matches(File.ReadAllText(f)).Select(m => m.Groups["code"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var unknown = ProductionHandlers
            .SelectMany(t => t.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: false).Cast<RequiresPermissionAttribute>())
            .Select(a => a.Permission)
            .Where(p => !seeded.Contains(p))
            .ToList();

        Assert.Contains("role:assign", seeded);
        Assert.True(unknown.Count == 0, "Permissions not seeded in db/migrations: " + string.Join(", ", unknown));
    }

    [GeneratedRegex(@"\('(?<code>[a-z_]+:[a-z_]+)',\s*'(READ|WRITE|SECURITY)'\)")]
    private static partial Regex SeededPermission();
}
