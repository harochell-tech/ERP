using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// FIN1-03: every acceptance test of the FIN-1 Frozen Baseline (§7) has at least one test tagged
/// <c>[Trait("AcceptanceFin1", "&lt;ID&gt;")]</c>, every tag names a baseline ID, and docs/acceptance/fin1.md lists them all.
/// </summary>
public sealed partial class AcceptanceFin1TraceabilityTests
{
    private static IReadOnlySet<string> BaselineIds()
        => File.ReadLines(Path.Combine(Repo.Root, "docs", "architecture", "fin1", "frozen-baseline-fin1.md"))
            .Select(l => BaselineRow().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> TaggedIds()
        => Repo.Files("tests", "*.cs")
            .SelectMany(f => Tag().Matches(File.ReadAllText(f)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_FIN1_acceptance_test_has_a_tagged_test()
    {
        var baseline = BaselineIds();
        var missing = baseline.Except(TaggedIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(baseline.Count >= 7, $"Only {baseline.Count} FIN-1 acceptance IDs were found; the parser no longer reads §7.");
        Assert.True(missing.Count == 0, "FIN-1 acceptance tests without a tagged test: " + string.Join(", ", missing));
    }

    [Fact]
    public void FIN1_tags_name_only_baseline_acceptance_tests()
    {
        var unknown = TaggedIds().Except(BaselineIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0, "AcceptanceFin1 tags that are not FIN-1 acceptance tests: " + string.Join(", ", unknown));
    }

    [Fact]
    public void The_FIN1_acceptance_matrix_lists_every_baseline_test()
    {
        var matrix = File.ReadAllText(Path.Combine(Repo.Root, "docs", "acceptance", "fin1.md"));

        var missing = BaselineIds().Where(id => !matrix.Contains($"| {id} |", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "docs/acceptance/fin1.md does not list: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"^\| (?<id>GL-[0-9]+) \|")]
    private static partial Regex BaselineRow();

    [GeneratedRegex(@"""AcceptanceFin1"", ""(?<id>[A-Z0-9]+-[A-Z0-9]+)""")]
    private static partial Regex Tag();
}
