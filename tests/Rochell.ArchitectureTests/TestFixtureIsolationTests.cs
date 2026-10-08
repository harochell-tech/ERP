using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>TST-01 / E-PR07-5: test fixtures (R-T1 stock issue, TEST.* rules, test permissions) never reach production migrations.</summary>
public sealed class TestFixtureIsolationTests
{
    [Trait("Acceptance", "TST-01")]
    [Theory]
    [InlineData("TEST.")]
    [InlineData("TestStock")]
    [InlineData("test:")]
    [InlineData("TEST_")]
    public void Production_migrations_contain_no_test_fixtures(string marker)
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Repo.Root, "db", "migrations"), "*.sql")
            .Where(f => Contains(File.ReadAllText(f), marker))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"'{marker}' found in production migrations: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// E-LAB1-01-16: «test:» is the resource of the test permissions (test:ping…), so it counts where a permission code starts — not
    /// inside a production resource that ends in «test» (lab_test:record, E-LAB1-9).
    /// </summary>
    private static bool Contains(string sql, string marker)
    {
        for (var at = sql.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = sql.IndexOf(marker, at + 1, StringComparison.Ordinal))
        {
            if (marker != "test:" || at == 0 || !(char.IsAsciiLetterLower(sql[at - 1]) || sql[at - 1] == '_'))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Test_fixture_migrations_exist_only_under_tests()
    {
        var fixtures = Directory.EnumerateFiles(Path.Combine(Repo.Root, "tests", "migrations"), "*.sql").ToList();

        Assert.True(fixtures.Any(f => File.ReadAllText(f).Contains("TEST.ISSUE", StringComparison.Ordinal)), "The R-T1 fixture must live in tests/migrations.");
    }
}
