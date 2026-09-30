using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// E-QUO1-04-9: every acceptance test of the QUO-1 baseline (§6) has at least one test tagged
/// <c>[Trait("AcceptanceQuo1", "&lt;ID&gt;")]</c>, every tag names a baseline ID, and docs/acceptance/quo1.md lists them all.
/// </summary>
public sealed partial class AcceptanceQuo1TraceabilityTests
{
    /// <summary>IDs whose test is still to come (none); a tagged ID listed here fails.</summary>
    private static readonly IReadOnlySet<string> Pending = new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> BaselineIds()
        => File.ReadLines(Path.Combine(Repo.Root, "docs", "architecture", "quo1", "frozen-baseline-quo1.md"))
            .Select(l => BaselineRow().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> TaggedIds()
        => Repo.Files("tests", "*.cs")
            .SelectMany(f => Tag().Matches(File.ReadAllText(f)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_QUO1_acceptance_test_has_a_tagged_test_or_is_pending()
    {
        var baseline = BaselineIds();
        var tagged = TaggedIds();
        var missing = baseline.Except(tagged).Except(Pending).Order(StringComparer.Ordinal).ToList();
        var stale = Pending.Intersect(tagged).Order(StringComparer.Ordinal).ToList();

        Assert.True(baseline.Count >= 8, $"Only {baseline.Count} QUO-1 acceptance IDs were found; the parser no longer reads §6.");
        Assert.True(missing.Count == 0, "QUO-1 acceptance tests without a tagged test: " + string.Join(", ", missing));
        Assert.True(stale.Count == 0, "Tagged tests still listed as pending: " + string.Join(", ", stale));
    }

    [Fact]
    public void QUO1_tags_name_only_baseline_acceptance_tests()
    {
        var unknown = TaggedIds().Except(BaselineIds()).Order(StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0, "AcceptanceQuo1 tags that are not QUO-1 acceptance tests: " + string.Join(", ", unknown));
    }

    [Fact]
    public void The_QUO1_acceptance_matrix_lists_every_baseline_test()
    {
        var matrix = File.ReadAllText(Path.Combine(Repo.Root, "docs", "acceptance", "quo1.md"));

        var missing = BaselineIds().Where(id => !matrix.Contains($"| {id} |", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "docs/acceptance/quo1.md does not list: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"^\| (?<id>QUO-[0-9]+|E2E-Q[0-9]+) \|")]
    private static partial Regex BaselineRow();

    [GeneratedRegex(@"""AcceptanceQuo1"", ""(?<id>[A-Z0-9]+-[A-Z0-9]+)""")]
    private static partial Regex Tag();
}
