using Npgsql;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// MFG3-01 (E-MFG3-3/5/8/9, E-MFG3-01-1…3): what Core keeps of the portal's stoppages, maintenance windows and daily reports is never
/// changed; ideal cycles and done maintenance are never changed either; a done task comes from the portal or from a person, not both.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MaintenanceSchemaTests(PostgresFixture postgres)
{
    private static async Task<PostgresException?> TryAsync(TestHarness h, string sql)
    {
        var tx = await h.OpenAppTransactionAsync();
        await using (tx.Connection)
        {
            try
            {
                await Sql.ExecuteAsync(tx.Connection, tx.Transaction, sql, CancellationToken.None);
                await tx.Transaction.CommitAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex;
            }
        }
    }

    [Fact]
    public async Task Readings_ideal_cycles_and_done_tasks_are_kept_and_never_changed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);
        var task = Guid.CreateVersion7();
        var hash = "decode(repeat('ab', 32), 'hex')";

        Assert.Null(await TryAsync(h, $"""
            INSERT INTO mfg.portal_stoppage (stoppage_row_id, company_id, portal_id, portal_code, started_at, ended_at, duration_seconds, reason, content_sha256, fetched_at)
            VALUES (gen_random_uuid(), '{h.CompanyId}', 7, 'planta2', '2026-10-08 11:00', '2026-10-08 11:40', 2400, 'fallo_mecanico', {hash}, now());
            INSERT INTO mfg.ideal_cycle (company_id, machine_id, item_id, valid_from, seconds, set_by, set_at) VALUES ('{h.CompanyId}', '{s.Machine}', '{s.Block}', '2026-10-01', 11.500, '{h.UserId}', now());
            INSERT INTO mfg.maintenance_task (task_id, company_id, machine_id, code, name, frequency_kind, every, status, created_by, created_at, version)
            VALUES ('{task}', '{h.CompanyId}', '{s.Machine}', 'ZAPATAS', 'Cambiar zapatas', 'CYCLES', 40000, 'ACTIVE', '{h.UserId}', now(), 1);
            INSERT INTO mfg.maintenance_done (done_id, company_id, task_id, done_at, source, portal_maintenance_id, recorded_at)
            VALUES (gen_random_uuid(), '{h.CompanyId}', '{task}', '2026-10-08 15:00', 'PORTAL', 3, now());
            """));
        var sameVersion = await TryAsync(h, $"""
            INSERT INTO mfg.portal_stoppage (stoppage_row_id, company_id, portal_id, portal_code, started_at, content_sha256, fetched_at)
            VALUES (gen_random_uuid(), '{h.CompanyId}', 7, 'planta2', '2026-10-08 11:00', {hash}, now())
            """);
        var stoppage = await TryAsync(h, "UPDATE mfg.portal_stoppage SET reason = 'otro'");
        var cycle = await TryAsync(h, "UPDATE mfg.ideal_cycle SET seconds = 10");
        var both = await TryAsync(h, $"""
            INSERT INTO mfg.maintenance_done (done_id, company_id, task_id, done_at, source, portal_maintenance_id, recorded_by, recorded_at)
            VALUES (gen_random_uuid(), '{h.CompanyId}', '{task}', '2026-10-09 15:00', 'PORTAL', 4, '{h.UserId}', now())
            """);
        var zero = await TryAsync(h, $"""
            INSERT INTO mfg.maintenance_task (task_id, company_id, machine_id, code, name, frequency_kind, every, status, created_by, created_at, version)
            VALUES (gen_random_uuid(), '{h.CompanyId}', '{s.Machine}', 'MOLDE', 'Revisar molde', 'DAYS', 0, 'ACTIVE', '{h.UserId}', now(), 1)
            """);

        Assert.Equal(SqlStates.UniqueViolation, sameVersion?.SqlState); // the same content is one version
        Assert.Equal("42501", stoppage?.SqlState);
        Assert.Equal("42501", cycle?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, both?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, zero?.SqlState);
        Assert.Equal("GERENTE_PLANTA", await h.ScalarAsync<string>(
            "SELECT string_agg(r.code, ',') FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE rp.permission_code = 'maintenance_plan:manage' AND r.code <> 'SUPERADMIN'"));
    }
}
