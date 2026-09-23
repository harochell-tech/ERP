using Rochell.Migrations.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Migrations.Tests;

public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void Main_migrations_are_valid_and_contiguous()
    {
        var scripts = MigrationDiscovery.Discover(TestPaths.MainSource);

        Assert.Equal(TestPaths.MainMigrationFiles, scripts.Select(s => s.FileName));
        Assert.Equal(["0001__extensions.sql", "0002__md_company.sql", "0003__core_platform.sql"], scripts.Take(3).Select(s => s.FileName));
    }

    [Fact]
    public void Checksum_is_identical_for_crlf_lf_and_bom_variants()
    {
        var lf = MigrationDiscovery.ComputeChecksum(MigrationDiscovery.Normalize("SELECT 1;\nSELECT 2;\n"));
        var crlf = MigrationDiscovery.ComputeChecksum(MigrationDiscovery.Normalize("SELECT 1;\r\nSELECT 2;\r\n"));
        var bom = MigrationDiscovery.ComputeChecksum(MigrationDiscovery.Normalize("\uFEFFSELECT 1;\nSELECT 2;\n"));

        Assert.Equal(lf, crlf);
        Assert.Equal(lf, bom);
    }

    [Theory]
    [InlineData("3__bad.sql")]
    [InlineData("0099_single_underscore.sql")]
    [InlineData("0099__CamelCase.sql")]
    [InlineData("0099__trailing_.sql")]
    public void Invalid_file_names_are_rejected(string fileName)
    {
        using var scratch = new ScratchMigrations();
        scratch.Write(fileName, "SELECT 1;");

        var ex = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(scratch.Source));
        Assert.Contains("Invalid migration file name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_versions_are_rejected()
    {
        using var scratch = new ScratchMigrations();
        scratch.Write("0002__another.sql", "SELECT 1;");

        var ex = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(scratch.Source));
        Assert.Contains("Duplicate migration version 0002", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gaps_in_versions_are_rejected()
    {
        using var scratch = new ScratchMigrations();
        scratch.Write(ScratchMigrations.NextFile("skipped_one", offset: 1), "SELECT 1;");

        var ex = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(scratch.Source));
        Assert.Contains("contiguous", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_migrations_are_rejected()
    {
        using var scratch = new ScratchMigrations();
        scratch.Write(ScratchMigrations.NextFile("empty"), "  \n");

        var ex = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(scratch.Source));
        Assert.Contains("is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_sql_files_are_ignored()
    {
        using var scratch = new ScratchMigrations();
        scratch.Write("README.md", "notes");

        Assert.Equal(TestPaths.MainMigrationFiles.Count, MigrationDiscovery.Discover(scratch.Source).Count);
    }
}
