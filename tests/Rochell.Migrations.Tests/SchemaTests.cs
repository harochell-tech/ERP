using Npgsql;
using Rochell.Migrations.Tests.Infrastructure;
using Xunit;

namespace Rochell.Migrations.Tests;

/// <summary>PR-01 schema must match the frozen baseline exactly: btree_gist + md.company, nothing else.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    private async Task<string> MigratedDatabaseAsync()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        await Db.Runner(cs).MigrateAsync(TestPaths.MainSource);
        return cs;
    }

    [Fact]
    public async Task Server_is_postgresql_17()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();

        var versionNum = int.Parse(await Db.ScalarAsync<string>(cs, "SHOW server_version_num") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(versionNum, 170000, 179999);
    }

    [Fact]
    public async Task Btree_gist_extension_is_installed()
    {
        var cs = await MigratedDatabaseAsync();

        Assert.NotNull(await Db.ScalarAsync<string>(cs, "SELECT extversion FROM pg_extension WHERE extname = 'btree_gist'"));
    }

    [Fact]
    public async Task Company_columns_match_frozen_shape()
    {
        var cs = await MigratedDatabaseAsync();

        var columns = await Db.ScalarAsync<string>(cs, """
            SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY ordinal_position)
            FROM information_schema.columns WHERE table_schema = 'md' AND table_name = 'company'
            """);

        Assert.Equal("company_id:uuid:NO,rnc:text:NO,legal_name:text:NO", columns);
    }

    [Fact]
    public async Task Company_primary_key_and_rnc_unique_exist()
    {
        var cs = await MigratedDatabaseAsync();

        var constraints = await Db.ScalarAsync<string>(cs, """
            SELECT string_agg(conname || ':' || contype::text, ',' ORDER BY conname)
            FROM pg_constraint WHERE conrelid = 'md.company'::regclass AND contype IN ('p', 'u')
            """);

        Assert.Equal("company_pk:p,company_rnc_uq:u", constraints);
    }

    [Fact]
    public async Task Duplicate_rnc_is_rejected()
    {
        var cs = await MigratedDatabaseAsync();
        await Db.ExecuteAsync(cs, "INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000001', '101000001', 'Empresa A')");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            Db.ExecuteAsync(cs, "INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000002', '101000001', 'Empresa B')"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Theory]
    [InlineData("INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000003', NULL, 'Sin RNC')")]
    [InlineData("INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000004', '101000004', NULL)")]
    [InlineData("INSERT INTO md.company (rnc, legal_name) VALUES ('101000005', 'Sin id')")]
    public async Task Required_columns_are_enforced(string sql)
    {
        var cs = await MigratedDatabaseAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(cs, sql));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
    }

    [Fact]
    public async Task No_tables_outside_pr01_scope_exist()
    {
        var cs = await MigratedDatabaseAsync();

        var tables = await Db.ScalarAsync<string>(cs, """
            SELECT string_agg(table_schema || '.' || table_name, ',' ORDER BY table_schema, table_name)
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'migrations')
            """);

        Assert.Equal("md.company", tables);
    }
}
