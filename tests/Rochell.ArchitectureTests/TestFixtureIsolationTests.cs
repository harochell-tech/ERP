using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>TST-01 / E-PR07-5: test fixtures (R-T1 stock issue, TEST.* rules, test permissions) never reach production migrations.</summary>
public sealed class TestFixtureIsolationTests
{
    [Theory]
    [InlineData("TEST.")]
    [InlineData("TestStock")]
    [InlineData("test:")]
    [InlineData("TEST_")]
    public void Production_migrations_contain_no_test_fixtures(string marker)
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Repo.Root, "db", "migrations"), "*.sql")
            .Where(f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"'{marker}' found in production migrations: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Test_fixture_migrations_exist_only_under_tests()
    {
        var fixtures = Directory.EnumerateFiles(Path.Combine(Repo.Root, "tests", "migrations"), "*.sql").ToList();

        Assert.True(fixtures.Any(f => File.ReadAllText(f).Contains("TEST.ISSUE", StringComparison.Ordinal)), "The R-T1 fixture must live in tests/migrations.");
    }
}
