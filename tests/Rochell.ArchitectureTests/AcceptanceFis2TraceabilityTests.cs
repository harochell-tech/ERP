using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// E-FIS2-03-7: every acceptance test of the FIS-2 baseline (§3) has at least one test tagged
/// <c>[Trait("AcceptanceFis2", "&lt;ID&gt;")]</c>, every tag names a baseline ID, and docs/acceptance/fis2.md lists them all.
/// </summary>
public sealed partial class AcceptanceFis2TraceabilityTests
{
    /// <summary>IDs whose test is still to come (none); a tagged ID listed here fails.</summary>
    private static readonly IReadOnlySet<string> Pending = new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> BaselineIds()
        => File.ReadLines(Path.Combine(Repo.Root, "docs", "architecture", "fis2", "frozen-baseline-fis2.md"))
            .Select(l => BaselineRow().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> TaggedIds()
        => Repo.Files("tests", "*.cs")
            .SelectMany(f => Tag().Matches(File.ReadAllText(f)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_FIS2_acceptance_test_has_a_tagged_test_or_is_pending()
    {
        var baseline = BaselineIds();
        var tagged = TaggedIds();
        var missing = baseline.Except(tagged).Except(Pending).Order(StringComparer.Ordinal).ToList();
        var stale = Pending.Intersect(tagged).Order(StringComparer.Ordinal).ToList();

        Assert.True(baseline.Count >= 7, $"Only {baseline.Count} FIS-2 acceptance IDs were found; the parser no longer reads §3.");
        Assert.True(missing.Count == 0, "FIS-2 acceptance tests without a tagged test: " + string.Join(", ", missing));
        Assert.True(stale.Count == 0, "Tagged tests still listed as pending: " + string.Join(", ", stale));
    }

    [Fact]
    public void FIS2_tags_name_only_baseline_acceptance_tests()
    {
        var unknown = TaggedIds().Except(BaselineIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0, "AcceptanceFis2 tags that are not FIS-2 acceptance tests: " + string.Join(", ", unknown));
    }

    [Fact]
    public void The_FIS2_acceptance_matrix_lists_every_baseline_test()
    {
        var matrix = File.ReadAllText(Path.Combine(Repo.Root, "docs", "acceptance", "fis2.md"));

        var missing = BaselineIds().Where(id => !matrix.Contains($"| {id} |", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "docs/acceptance/fis2.md does not list: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"^\| (?<id>F2-[0-9]+|E2E-F2) \|")]
    private static partial Regex BaselineRow();

    [GeneratedRegex(@"""AcceptanceFis2"", ""(?<id>[A-Z0-9]+-[A-Z0-9]+)""")]
    private static partial Regex Tag();
}
