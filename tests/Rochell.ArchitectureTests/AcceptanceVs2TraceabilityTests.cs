using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// E-VS2-09-4: every acceptance test of the VS#2 Frozen Baseline (§9) has at least one test tagged
/// <c>[Trait("AcceptanceVs2", "&lt;ID&gt;")]</c>, every tag names a baseline ID, and docs/acceptance/vs2.md lists them all.
/// </summary>
public sealed partial class AcceptanceVs2TraceabilityTests
{
    private static IReadOnlySet<string> BaselineIds()
        => File.ReadLines(Path.Combine(Repo.Root, "docs", "architecture", "vs2", "frozen-baseline-vs2.md"))
            .Select(l => BaselineRow().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> TaggedIds()
        => Repo.Files("tests", "*.cs")
            .SelectMany(f => Tag().Matches(File.ReadAllText(f)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_VS2_acceptance_test_has_a_tagged_test()
    {
        var baseline = BaselineIds();
        var missing = baseline.Except(TaggedIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(baseline.Count >= 17, $"Only {baseline.Count} VS#2 acceptance IDs were found; the parser no longer reads §9.");
        Assert.True(missing.Count == 0, "VS#2 acceptance tests without a tagged test: " + string.Join(", ", missing));
    }

    [Fact]
    public void VS2_tags_name_only_baseline_acceptance_tests()
    {
        var unknown = TaggedIds().Except(BaselineIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0, "AcceptanceVs2 tags that are not VS#2 acceptance tests: " + string.Join(", ", unknown));
    }

    [Fact]
    public void The_VS2_acceptance_matrix_lists_every_baseline_test()
    {
        var matrix = File.ReadAllText(Path.Combine(Repo.Root, "docs", "acceptance", "vs2.md"));

        var missing = BaselineIds().Where(id => !matrix.Contains($"| {id} |", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "docs/acceptance/vs2.md does not list: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"^\| (?<id>(PAY|BNK|E2E)-[0-9]+|INV-P) \|")]
    private static partial Regex BaselineRow();

    [GeneratedRegex(@"""AcceptanceVs2"", ""(?<id>[A-Z0-9]+-[A-Z0-9]+)""")]
    private static partial Regex Tag();
}
