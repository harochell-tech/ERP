using System.Reflection;
using System.Text.RegularExpressions;
using Rochell.Identity.Sessions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// E-UX1-01-7: every error code the server can return has a Spanish message in web/src/lib/errors.ts. The server's catalogue is
/// the public string constants of every type whose name ends with "Errors" in the production assemblies, plus the two codes of
/// <see cref="SessionService"/> (sign-in and test identities).
/// </summary>
public sealed partial class ErrorCatalogueTests
{
    private static IReadOnlyDictionary<string, string> ServerCodes()
    {
        var codes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in Repo.ProductionAssemblies.SelectMany(LoadableTypes).Where(t => t.Name.EndsWith("Errors", StringComparison.Ordinal)))
        {
            foreach (var field in Constants(type))
            {
                codes.TryAdd((string)field.GetRawConstantValue()!, $"{type.Name}.{field.Name}");
            }
        }

        codes.TryAdd(SessionService.LoginRejected, $"{nameof(SessionService)}.{nameof(SessionService.LoginRejected)}");
        codes.TryAdd(SessionService.TestIdentityUnavailable, $"{nameof(SessionService)}.{nameof(SessionService.TestIdentityUnavailable)}");
        return codes;
    }

    private static IEnumerable<FieldInfo> Constants(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static IReadOnlySet<string> WebMessages()
    {
        var source = File.ReadAllText(Path.Combine(Repo.Root, "web", "src", "lib", "errors.ts"));
        var start = source.IndexOf("ERROR_MESSAGES", StringComparison.Ordinal);
        Assert.True(start >= 0, "web/src/lib/errors.ts no longer declares ERROR_MESSAGES.");
        var end = source.IndexOf("\n};", start, StringComparison.Ordinal);
        Assert.True(end > start, "The ERROR_MESSAGES object in web/src/lib/errors.ts is not closed by a line with '};'.");
        return MessageKey().Matches(source[start..end]).Select(m => m.Groups["code"].Value).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_server_error_code_has_a_Spanish_message()
    {
        var codes = ServerCodes();
        var messages = WebMessages();

        var missing = codes.Where(c => !messages.Contains(c.Key)).Select(c => $"{c.Key} ({c.Value})").ToList();

        Assert.True(codes.Count >= 250, $"Only {codes.Count} server error codes were found; the *Errors classes are no longer read.");
        Assert.True(missing.Count == 0, "Server error codes without a message in web/src/lib/errors.ts: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"^\s+(?<code>[A-Z][A-Z0-9_]*):", RegexOptions.Multiline)]
    private static partial Regex MessageKey();
}
