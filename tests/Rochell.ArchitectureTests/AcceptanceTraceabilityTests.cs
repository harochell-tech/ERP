using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// E-PR19-1: every acceptance test of the Frozen Baseline (§15) and its patches has at least one test tagged
/// <c>[Trait("Acceptance", "&lt;ID&gt;")]</c> (the load harness declares PF-01 with <c>AssemblyMetadata("Acceptance", …)</c>).
/// The matrix of IDs to tests is docs/acceptance/vs1.md.
/// </summary>
public sealed partial class AcceptanceTraceabilityTests
{
    private static IReadOnlySet<string> BaselineIds()
        => Directory.EnumerateFiles(Path.Combine(Repo.Root, "docs", "architecture", "baseline"), "*.md")
            .Where(f => Path.GetFileName(f) is var name && (name.StartsWith("04-", StringComparison.Ordinal) || name.StartsWith("05-", StringComparison.Ordinal) || name.StartsWith("06-", StringComparison.Ordinal)))
            .SelectMany(f => File.ReadLines(f))
            .Select(l => BaselineRow().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> TaggedIds()
        => Repo.Files("tests", "*.cs")
            .SelectMany(f => Tag().Matches(File.ReadAllText(f)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_baseline_acceptance_test_has_a_tagged_test()
    {
        var baseline = BaselineIds();
        var missing = baseline.Except(TaggedIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(baseline.Count >= 70, $"Only {baseline.Count} baseline IDs were found; the parser no longer reads the baseline tables.");
        Assert.True(missing.Count == 0, "Acceptance tests without a tagged test: " + string.Join(", ", missing));
    }

    [Fact]
    public void Tags_name_only_baseline_acceptance_tests()
    {
        var unknown = TaggedIds().Except(BaselineIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0, "Tags that are not baseline acceptance tests: " + string.Join(", ", unknown));
    }

    [Fact]
    public void The_acceptance_matrix_lists_every_baseline_test()
    {
        var matrix = File.ReadAllText(Path.Combine(Repo.Root, "docs", "acceptance", "vs1.md"));

        var missing = BaselineIds().Where(id => !matrix.Contains($"| {id} |", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "docs/acceptance/vs1.md does not list: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"^\| (?<id>(AT|RC|SI|ID|CC|IV|SC|PD|HS|EX|PF|TEN|INT|CMD|VAL|REV|TMP|FIS|RO|RL|TST)-[0-9]+b?) \|")]
    private static partial Regex BaselineRow();

    [GeneratedRegex(@"""Acceptance"", ""(?<id>[A-Z]+-[0-9]+b?)""")]
    private static partial Regex Tag();
}
