using Npgsql;
using Rochell.Platform.Commands;
using Rochell.Platform.Observability;
using Rochell.TestInfrastructure;

namespace Rochell.Platform.Tests.Infrastructure;

/// <summary>A migrated database, one company, the pipeline running as the application role.</summary>
internal sealed class PlatformHarness : IAsyncDisposable
{
    private PlatformHarness(TestDatabase database, NpgsqlDataSource app, NpgsqlDataSource admin, Guid companyId)
    {
        Database = database;
        App = app;
        Admin = admin;
        CompanyId = companyId;
        RequestLog = new RequestLogWriter(app, RequestLogErrors.Add);
        Pipeline = new CommandPipeline(app, RequestLog);
    }

    public TestDatabase Database { get; }

    public NpgsqlDataSource App { get; }

    public NpgsqlDataSource Admin { get; }

    public Guid CompanyId { get; }

    public Guid SessionId { get; } = Guid.CreateVersion7();

    public List<Exception> RequestLogErrors { get; } = [];

    public RequestLogWriter RequestLog { get; }

    public CommandPipeline Pipeline { get; }

    public static async Task<PlatformHarness> CreateAsync(PostgresFixture postgres)
    {
        var database = await postgres.CreateMigratedDatabaseAsync();
        var admin = NpgsqlDataSource.Create(database.AdminConnectionString);
        var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var companyId = await CreateCompanyAsync(admin);
        return new PlatformHarness(database, app, admin, companyId);
    }

    public static async Task<Guid> CreateCompanyAsync(NpgsqlDataSource admin)
    {
        var id = Guid.CreateVersion7();
        await using var command = admin.CreateCommand("INSERT INTO md.company (company_id, rnc, legal_name) VALUES (@id, @rnc, @name)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("rnc", id.ToString("N")[^11..]); // random part of the UUIDv7
        command.Parameters.AddWithValue("name", "Empresa de prueba");
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public PingCommand Ping(string key, string message = "hola", PingMode mode = PingMode.Normal, int sideEvents = 0, DateTime? occurredAt = null)
        => new(CompanyId, SessionId, key, message, mode, sideEvents, occurredAt);

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Admin.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value1 = await command.ExecuteScalarAsync();
        return value1 is null or DBNull ? default : (T)value1;
    }

    public Task<long> CountAsync(string table) => ScalarAsync<long>($"SELECT count(*) FROM {table}");

    public async Task<(long Commands, long Events, long Outbox)> CountsAsync()
        => (await CountAsync("core.command_log"), await CountAsync("core.domain_event"), await CountAsync("core.outbox"));

    public async Task<List<string>> OutcomesAsync()
    {
        await RequestLog.FlushAsync();
        await using var command = Admin.CreateCommand("SELECT outcome FROM obs.request_log ORDER BY received_at, outcome");
        await using var reader = await command.ExecuteReaderAsync();
        var outcomes = new List<string>();
        while (await reader.ReadAsync())
        {
            outcomes.Add(reader.GetString(0));
        }

        return outcomes;
    }

    /// <summary>Runs SQL on a fresh application-role connection (autocommit) and returns the PostgreSQL error, if any.</summary>
    public async Task<PostgresException?> AppExecuteAsync(string sql)
    {
        try
        {
            await using var command = App.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    public async Task<PostgresException?> AdminExecuteAsync(string sql)
    {
        try
        {
            await using var command = Admin.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Admin.DisposeAsync();
    }
}
