using Npgsql;
using Rochell.Migrations.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Migrations.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class MigrationRunnerTests(PostgresFixture postgres)
{
    private static int MainCount => TestPaths.MainMigrationFiles.Count;

    [Fact]
    public async Task Empty_database_gets_all_main_migrations_in_order()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();

        var result = await Db.Runner(cs).MigrateAsync(TestPaths.MainSource);

        Assert.Equal(0, result.AlreadyApplied);
        Assert.Equal(TestPaths.MainMigrationFiles, result.AppliedNow);
        Assert.Equal((long)MainCount, await Db.ScalarAsync<long>(cs, "SELECT count(*) FROM migrations.applied_migration WHERE source = 'main'"));
    }

    [Fact]
    public async Task Second_run_is_a_no_op()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        var runner = Db.Runner(cs);
        await runner.MigrateAsync(TestPaths.MainSource);

        var second = await runner.MigrateAsync(TestPaths.MainSource);

        Assert.Equal(MainCount, second.AlreadyApplied);
        Assert.Empty(second.AppliedNow);
    }

    [Fact]
    public async Task Verify_reports_pending_without_applying()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();

        var result = await Db.Runner(cs).VerifyAsync(TestPaths.MainSource);

        Assert.Equal(MainCount, result.Pending.Count);
        Assert.Null(await Db.ScalarAsync<string>(cs, "SELECT to_regclass('md.company')::text"));
    }

    [Fact]
    public async Task Modified_applied_migration_is_rejected()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        using var scratch = new ScratchMigrations();
        await Db.Runner(cs).MigrateAsync(scratch.Source);

        scratch.Append("0002__md_company.sql", "\n-- harmless looking edit\n");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Db.Runner(cs).MigrateAsync(scratch.Source));
        Assert.Contains("was modified after being applied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleted_applied_migration_is_rejected()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        using var scratch = new ScratchMigrations();
        var extra = ScratchMigrations.NextFile("extra");
        scratch.Write(extra, "CREATE TABLE md.scratch_extra (id integer);");
        await Db.Runner(cs).MigrateAsync(scratch.Source);

        scratch.Delete(extra);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Db.Runner(cs).MigrateAsync(scratch.Source));
        Assert.Contains("no longer exists on disk", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_migration_is_rolled_back_and_not_journaled()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        using var scratch = new ScratchMigrations();
        var broken = ScratchMigrations.NextFile("broken");
        scratch.Write(broken, "CREATE TABLE md.scratch_partial (id integer);\nSELECT 1 / 0;");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Db.Runner(cs).MigrateAsync(scratch.Source));

        Assert.Contains(broken, ex.Message, StringComparison.Ordinal);
        Assert.Null(await Db.ScalarAsync<string>(cs, "SELECT to_regclass('md.scratch_partial')::text"));
        Assert.Equal(MainCount, await Db.ScalarAsync<int>(cs, "SELECT max(version) FROM migrations.applied_migration WHERE source = 'main'"));
    }

    [Fact]
    public async Task Concurrent_runners_apply_each_migration_exactly_once()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();

        var results = await Task.WhenAll(
            Db.Runner(cs).MigrateAsync(TestPaths.MainSource),
            Db.Runner(cs).MigrateAsync(TestPaths.MainSource),
            Db.Runner(cs).MigrateAsync(TestPaths.MainSource));

        Assert.Equal(MainCount, results.Sum(r => r.AppliedNow.Count));
        Assert.Equal((long)MainCount, await Db.ScalarAsync<long>(cs, "SELECT count(*) FROM migrations.applied_migration"));
    }

    [Fact]
    public async Task Main_and_test_sources_are_journaled_separately()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        var runner = Db.Runner(cs);

        await runner.MigrateAsync(TestPaths.MainSource);
        await runner.MigrateAsync(TestPaths.TestSource);

        Assert.Equal(0L, await Db.ScalarAsync<long>(cs, "SELECT count(*) FROM migrations.applied_migration WHERE source = 'test'"));
        Assert.Equal((long)MainCount, await Db.ScalarAsync<long>(cs, "SELECT count(*) FROM migrations.applied_migration WHERE source = 'main'"));
    }

    [Theory]
    [InlineData("UPDATE migrations.applied_migration SET name = 'x'")]
    [InlineData("DELETE FROM migrations.applied_migration")]
    public async Task Journal_is_append_only(string sql)
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        await Db.Runner(cs).MigrateAsync(TestPaths.MainSource);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(cs, sql));
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }
}
