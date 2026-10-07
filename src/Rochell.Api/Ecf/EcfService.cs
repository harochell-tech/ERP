using Rochell.Api.Hosting;
using Rochell.Identity;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Tax.Ecf;

namespace Rochell.Api.Ecf;

/// <summary>
/// E-VS4-02-1: every <see cref="EcfSettings.Interval"/> takes the e-CF queue of every company — those whose next step is due, and the
/// accepted ones still without their signed files — and runs one <see cref="AdvanceEcfDocument"/> each through the command pipeline,
/// as the daily process (PROCESO_DIARIO) on a SERVICE session opened only when there is work. Each step locks its e-CF, so two servers
/// never take the same one; the idempotency key is the e-CF and its version.
/// </summary>
public sealed class EcfService(EcfSettings settings, AppDatabase database, SessionService sessions, CommandPipeline pipeline, IServiceProvider services, ILogger<EcfService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("e-CF gateway in mode {Mode}.", settings.Mode);
        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "e-CF pass failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass; returns the steps run.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid Company, Guid Document, long Version)> due;
        await using (var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            due = await Reading.ListAsync(
                connection,
                null,
                """
                SELECT d.company_id, d.document_id, d.version FROM tax.ecf_document d
                WHERE (d.status IN ('PENDING', 'SUBMITTED', 'UNKNOWN_OUTCOME', 'CONTINGENCY') AND d.next_poll_at <= now())
                   OR (d.status IN ('ACCEPTED', 'ACCEPTED_CONDITIONAL') AND d.finished_at > now() - interval '7 days'
                       AND (SELECT count(*) FROM tax.ecf_file f WHERE f.document_id = d.document_id) < 2)
                ORDER BY d.next_poll_at NULLS LAST, d.created_at
                LIMIT 200
                """,
                r => (r.GetGuid(0), r.GetGuid(1), r.GetInt64(2)),
                cancellationToken).ConfigureAwait(false);
        }

        if (due.Count == 0)
        {
            return 0;
        }

        var session = await sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId, cancellationToken).ConfigureAwait(false);
        var steps = 0;
        try
        {
            var unauthorized = new HashSet<Guid>();
            foreach (var (company, document, version) in due)
            {
                if (unauthorized.Contains(company))
                {
                    continue;
                }

                try
                {
                    var handler = services.GetRequiredService<AdvanceEcfDocumentHandler>();
                    await pipeline.ExecuteAsync(new AdvanceEcfDocument(company, session, $"ecf:{document}:{version}", document), handler, Guid.CreateVersion7(), cancellationToken)
                        .ConfigureAwait(false);
                    steps++;
                }
                catch (DomainException ex) when (ex.Code == AuthorizationErrors.NotAuthorized)
                {
                    unauthorized.Add(company);
                    logger.LogWarning("Company {Company} has no PROCESO_DIARIO assignment; its e-CF are not sent.", company);
                }
                catch (DomainException ex)
                {
                    logger.LogError("e-CF {Document} step refused: {Code} {Message}", document, ex.Code, ex.Message);
                }
            }
        }
        finally
        {
            await sessions.EndSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        return steps;
    }
}
