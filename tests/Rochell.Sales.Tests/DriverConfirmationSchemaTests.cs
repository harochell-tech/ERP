using Npgsql;
using Rochell.Identity;
using Rochell.Platform.Data;
using Rochell.Sales.Orders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// ENT1-01 (E-ENT-1…8, E-ENT1-01-1…10): what the database keeps of the drivers' confirmations — one link per delivery, PIN tries and
/// confirmations never changed, the phone's time only within its bounds, and the service identity that only confirms.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DriverConfirmationSchemaTests(PostgresFixture postgres)
{
    private static string Link(Guid company, Guid delivery, string status = "ACTIVE", int failed = 0) =>
        $"""
        INSERT INTO log.delivery_link (company_id, delivery_id, generation, status, expires_at, failed_attempts, created_at, version)
        VALUES ('{company}', '{delivery}', 1, '{status}', now() + interval '7 days', {failed}, now(), 1)
        """;

    private static string Confirmation(Guid company, Guid delivery, Guid driver, Guid evt, string outcome, string? note, string phoneAt, string confirmedAt) =>
        $"""
        INSERT INTO log.driver_confirmation (confirmation_id, company_id, delivery_id, generation, driver_id, receiver_name, outcome, note, phone_at, server_at,
                                             confirmed_at, latitude, longitude, accuracy_m, evidence_kind, evidence_ref, evidence_sha256, client_address, event_id)
        VALUES ('{Guid.CreateVersion7()}', '{company}', '{delivery}', 1, '{driver}', 'Ing. María Gómez', '{outcome}', {(note is null ? "NULL" : $"'{note}'")},
                {phoneAt}, now(), {confirmedAt}, 18.583100, -68.404700, 12.50, 'PHOTO', 'entregas/x/y/z.jpg', decode(repeat('ab', 32), 'hex'), '10.0.0.7', '{evt}')
        """;

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
    public async Task A_confirmation_is_kept_once_with_bounded_times_and_never_changed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (order, line) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 600m);
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, s, order, line, 600m, own: true, "d");
        var evt = await h.ScalarAsync<Guid>("SELECT event_id FROM core.domain_event WHERE company_id = @c ORDER BY recorded_at DESC LIMIT 1", ("c", h.CompanyId));

        var lockedEarly = await TryAsync(h, Link(h.CompanyId, delivery, "LOCKED", 3));
        Assert.Null(await TryAsync(h, Link(h.CompanyId, delivery)));
        var silentDifferences = await TryAsync(h, Confirmation(h.CompanyId, delivery, s.Driver, evt, "DIFFERENCES", null, "NULL", "now()"));
        var phoneAhead = await TryAsync(h, Confirmation(h.CompanyId, delivery, s.Driver, evt, "FULL", null, "now() + interval '1 hour'", "now() + interval '1 hour'"));
        Assert.Null(await TryAsync(h, Confirmation(h.CompanyId, delivery, s.Driver, evt, "FULL", null, "now() - interval '3 minutes'", "now() - interval '3 minutes'")));
        var twice = await TryAsync(h, Confirmation(h.CompanyId, delivery, s.Driver, evt, "FULL", null, "NULL", "now()"));
        var changed = await TryAsync(h, "UPDATE log.driver_confirmation SET receiver_name = 'Otro'");

        Assert.Equal(SqlStates.CheckViolation, lockedEarly?.SqlState); // LOCKED only after 5 failed PINs
        Assert.Equal(SqlStates.CheckViolation, silentDifferences?.SqlState); // differences say what happened
        Assert.Equal(SqlStates.CheckViolation, phoneAhead?.SqlState); // E-ENT1-01-6: at most 5 minutes ahead of the server
        Assert.Equal(SqlStates.UniqueViolation, twice?.SqlState); // one confirmation per link generation
        Assert.Equal("42501", changed?.SqlState); // the application may not update it at all
    }

    [Fact]
    public async Task Dispatch_sets_pins_and_reopens_links_and_the_confirmation_identity_only_confirms()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var dispatch = await h.ScalarAsync<string>(
            "SELECT string_agg(rp.permission_code, ',' ORDER BY rp.permission_code) FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE r.code = 'DESPACHO' AND rp.permission_code LIKE 'd%'");
        var service = await h.ScalarAsync<string>(
            "SELECT string_agg(rp.permission_code, ',') FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE r.code = 'CONFIRMACION_ENTREGA'");
        await h.GrantAsync(h.CompanyId, IdentityConstants.DeliveryConfirmationUserId, "CONFIRMACION_ENTREGA");
        var toPerson = await Record.ExceptionAsync(async () => await h.GrantAsync(h.CompanyId, await h.CreateUserAsync(), "CONFIRMACION_ENTREGA"));
        var other = await Record.ExceptionAsync(() => h.GrantAsync(h.CompanyId, IdentityConstants.DeliveryConfirmationUserId, "DESPACHO"));

        Assert.Equal("delivery:email,delivery:manage,delivery_link:reopen,driver_pin:manage", dispatch);
        Assert.Equal("delivery:driver_confirm", service);
        Assert.Contains("only for its service identity", toPerson?.Message, StringComparison.Ordinal);
        Assert.Contains("holds only its own service role", other?.Message, StringComparison.Ordinal);
    }
}
