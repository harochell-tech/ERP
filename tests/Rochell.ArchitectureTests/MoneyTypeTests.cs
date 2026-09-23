using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>ADR-015: no floating point in production code or schema. Complements the RS0030 analyzer (build-time).</summary>
public sealed partial class MoneyTypeTests
{
    private static readonly Type[] FloatingPoint = [typeof(float), typeof(double), typeof(Half)];

    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Production_assemblies_declare_no_floating_point_members()
    {
        var violations = new List<string>();
        foreach (var assembly in Repo.ProductionAssemblies)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                foreach (var field in type.GetFields(All))
                {
                    Check(field.FieldType, $"{type.FullName}.{field.Name} (field)", violations);
                }

                foreach (var property in type.GetProperties(All))
                {
                    Check(property.PropertyType, $"{type.FullName}.{property.Name} (property)", violations);
                }

                foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    if (method is MethodInfo info)
                    {
                        Check(info.ReturnType, $"{type.FullName}.{method.Name} (return)", violations);
                    }

                    foreach (var parameter in method.GetParameters())
                    {
                        Check(parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})", violations);
                    }

                    foreach (var local in method.GetMethodBody()?.LocalVariables ?? [])
                    {
                        Check(local.LocalType, $"{type.FullName}.{method.Name} (local #{local.LocalIndex})", violations);
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Floating point types found (ADR-015):\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Banned_symbols_file_bans_all_floating_point_types()
    {
        var banned = File.ReadAllText(Path.Combine(Repo.Src, "BannedSymbols.txt"));

        Assert.Contains("T:System.Double;", banned, StringComparison.Ordinal);
        Assert.Contains("T:System.Single;", banned, StringComparison.Ordinal);
        Assert.Contains("T:System.Half;", banned, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_migrations_use_no_floating_point_or_money_types()
    {
        var violations = new List<string>();
        foreach (var file in Repo.Files("db", "*.sql").Concat(Repo.Files(Path.Combine("tests", "migrations"), "*.sql")))
        {
            var code = StripCommentsAndLiterals(File.ReadAllText(file));
            foreach (Match match in BannedSqlType().Matches(code))
            {
                violations.Add($"{Path.GetFileName(file)}: '{match.Value}'");
            }
        }

        Assert.True(violations.Count == 0, "Banned SQL numeric types (use numeric, ADR-015):\n" + string.Join('\n', violations));
    }

    private static void Check(Type type, string location, List<string> violations)
    {
        if (ContainsFloatingPoint(type))
        {
            violations.Add($"{location}: {type}");
        }
    }

    private static bool ContainsFloatingPoint(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            return ContainsFloatingPoint(type.GetElementType()!);
        }

        if (FloatingPoint.Contains(type))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsFloatingPoint);
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static string StripCommentsAndLiterals(string sql)
    {
        var withoutBlock = BlockComment().Replace(sql, " ");
        var withoutLine = LineComment().Replace(withoutBlock, " ");
        return StringLiteral().Replace(withoutLine, "''");
    }

    [GeneratedRegex(@"\b(real|float4|float8|money)\b|\bdouble\s+precision\b|\bfloat\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BannedSqlType();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();
}
