using Npgsql;
using Rochell.Identity;
using Rochell.Identity.Authorization;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Observability;
using Rochell.Platform.Time;

namespace Rochell.TestInfrastructure;

/// <summary>
/// A migrated database with one company and one user who holds TEST_PINGER through an open session.
/// The pipeline runs as the application role with the real SQL authorizer and row-level security.
/// </summary>
public sealed class TestHarness : IAsyncDisposable
{
    public const string HostedDomain = "rochell.com.do";

    private TestHarness(TestDatabase database, NpgsqlDataSource app, NpgsqlDataSource admin, IClock clock)
    {
        Database = database;
        App = app;
        Admin = admin;
        Clock = clock;
        Options = new IdentityOptions { HostedDomain = HostedDomain };
        RequestLog = new RequestLogWriter(app, RequestLogErrors.Add);
        Pipeline = new CommandPipeline(app, new SqlCommandAuthorizer(Options, clock), RequestLog, clock);
        Sessions = new SessionService(app, Options, clock);
    }

    public TestDatabase Database { get; }

    public NpgsqlDataSource App { get; }

    public NpgsqlDataSource Admin { get; }

    public IClock Clock { get; }

    public IdentityOptions Options { get; }

    public Guid CompanyId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid SessionId { get; private set; }

    public List<Exception> RequestLogErrors { get; } = [];

    public RequestLogWriter RequestLog { get; }

    public CommandPipeline Pipeline { get; }

    public SessionService Sessions { get; }

    public static async Task<TestHarness> CreateAsync(PostgresFixture postgres, IClock? clock = null)
    {
        var database = await postgres.CreateMigratedDatabaseAsync();
        var harness = new TestHarness(
            database,
            NpgsqlDataSource.Create(database.AppConnectionString),
            NpgsqlDataSource.Create(database.AdminConnectionString),
            clock ?? SystemClock.Instance);
        harness.CompanyId = await harness.CreateCompanyAsync();
        harness.UserId = await harness.CreateUserAsync();
        await harness.GrantAsync(harness.CompanyId, harness.UserId, "TEST_PINGER");
        harness.SessionId = await harness.CreateSessionAsync(harness.UserId);
        return harness;
    }

    public async Task<Guid> CreateCompanyAsync()
    {
        var id = Guid.CreateVersion7();
        await using var command = Admin.CreateCommand("INSERT INTO md.company (company_id, rnc, legal_name) VALUES (@id, @rnc, @name)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("rnc", id.ToString("N")[^11..]); // random part of the UUIDv7
        command.Parameters.AddWithValue("name", "Empresa de prueba");
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Creates an active human user; returns its id. The OIDC subject is "sub-{id}".</summary>
    public async Task<Guid> CreateUserAsync(string status = "ACTIVE")
    {
        var id = Guid.CreateVersion7();
        await using var command = Admin.CreateCommand(
            "INSERT INTO iam.user (user_id, kind, employee_id, email, oidc_subject, status) VALUES (@id, 'HUMAN', @employee, @email, @subject, @status)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("employee", Guid.CreateVersion7());
        command.Parameters.AddWithValue("email", $"{id:N}@{HostedDomain}");
        command.Parameters.AddWithValue("subject", SubjectOf(id));
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public static string SubjectOf(Guid userId) => $"sub-{userId:N}";

    public static OidcClaims ClaimsOf(Guid userId) => new(SubjectOf(userId), $"{userId:N}@{HostedDomain}", EmailVerified: true, HostedDomain);

    /// <summary>Opens a session through the real session service (login counts as re-authentication).</summary>
    public Task<Guid> CreateSessionAsync(Guid userId) => Sessions.StartOidcSessionAsync(ClaimsOf(userId), null, "tests");

    /// <summary>Bootstrap grant as the deployment role (like `rochell-migrate grant-role`).</summary>
    public async Task GrantAsync(Guid companyId, Guid userId, string roleCode, Guid? plantId = null)
    {
        await using var command = Admin.CreateCommand(
            """
            INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
            SELECT @id, @company, @user, role_id, @plant, @valid_from, '00000000-0000-7000-8000-00000000d001'
            FROM iam.role WHERE code = @role
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("plant", (object?)plantId ?? DBNull.Value);
        command.Parameters.AddWithValue("valid_from", Clock.UtcNow.AddMinutes(-1));
        command.Parameters.AddWithValue("role", roleCode);
        if (await command.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException($"Role {roleCode} not found.");
        }
    }

    public PingCommand Ping(string key, string message = "hola", PingMode mode = PingMode.Normal, int sideEvents = 0, DateTime? occurredAt = null)
        => new(CompanyId, SessionId, key, message, mode, sideEvents, occurredAt);

    public Task<CommandResult> RunAsync<TCommand>(TCommand command, ICommandHandler<TCommand> handler)
        where TCommand : ICommand
        => Pipeline.ExecuteAsync(command, handler, Guid.CreateVersion7());

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Admin.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
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

    /// <summary>Opens an application-role transaction with the tenant set (row-level security), like the pipeline does.</summary>
    public async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> OpenAppTransactionAsync(Guid? companyId = null)
    {
        var connection = await App.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var tenant = new NpgsqlCommand("SELECT set_config('app.company_id', @c, true)", connection, transaction);
        tenant.Parameters.AddWithValue("c", (companyId ?? CompanyId).ToString());
        await tenant.ExecuteNonQueryAsync();
        return (connection, transaction);
    }

    /// <summary>Runs SQL as the application role inside a tenant transaction; returns the PostgreSQL error, if any.</summary>
    public async Task<PostgresException?> AppExecuteAsync(string sql, Guid? companyId = null)
    {
        var (connection, transaction) = await OpenAppTransactionAsync(companyId);
        await using (connection)
        await using (transaction)
        {
            try
            {
#pragma warning disable CA2100 // Test SQL literals.
                await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex;
            }
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
