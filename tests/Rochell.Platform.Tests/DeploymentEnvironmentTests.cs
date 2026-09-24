using Rochell.Platform.Data;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>Frozen Baseline Patch 1.1, correction 3: the environment cannot be spoofed by the application role.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DeploymentEnvironmentTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";

    [Fact]
    public async Task Empty_table_yields_null_environment()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        await using var command = h.App.CreateCommand("SELECT core.current_environment()");
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Application_role_cannot_write_or_spoof_the_environment()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        Assert.Null(await h.AdminExecuteAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('PRODUCTION', current_user, now())"));

        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync("UPDATE core.deployment_environment SET environment = 'TEST'"))?.SqlState);
        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync("DELETE FROM core.deployment_environment"))?.SqlState);
        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync("INSERT INTO core.deployment_environment (singleton, environment, set_by, set_at) VALUES (true, 'TEST', 'x', now())"))?.SqlState);

        await using var connection = await h.App.OpenConnectionAsync();
        await using (var spoof = connection.CreateCommand())
        {
            spoof.CommandText = "SET app.environment = 'TEST'";
            await spoof.ExecuteNonQueryAsync();
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT core.current_environment()";
        Assert.Equal("PRODUCTION", await read.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("UPDATE core.deployment_environment SET environment = 'TEST'")]
    [InlineData("DELETE FROM core.deployment_environment")]
    [InlineData("TRUNCATE core.deployment_environment")]
    public async Task Even_the_owner_cannot_change_it_without_a_migration(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        Assert.Null(await h.AdminExecuteAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('TEST', current_user, now())"));

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
    }

    [Fact]
    public async Task Only_one_row_and_only_known_values()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(SqlStates.CheckViolation, (await h.AdminExecuteAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('STAGING', current_user, now())"))?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await h.AdminExecuteAsync("INSERT INTO core.deployment_environment (singleton, environment, set_by, set_at) VALUES (false, 'TEST', current_user, now())"))?.SqlState);
        Assert.Null(await h.AdminExecuteAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('TEST', current_user, now())"));
        Assert.Equal(SqlStates.UniqueViolation, (await h.AdminExecuteAsync("INSERT INTO core.deployment_environment (environment, set_by, set_at) VALUES ('PRODUCTION', current_user, now())"))?.SqlState);
    }
}
