using Rochell.Identity;
using Rochell.Identity.Sessions;
using Rochell.Manufacturing.Portal;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Api.Hosting;

/// <summary>
/// MFG2-02 (E-MFG2-1/6): every <see cref="PortalSettings.Interval"/> reads the machines' portal for each company holding the daily
/// process and prepares the DRAFT shift summaries (<see cref="PortalRunner"/>), on a SERVICE session opened for the pass. Off without
/// <c>Rochell:Portal:BaseUrl</c>.
/// </summary>
public sealed class PortalService(PortalSettings settings, IPortalSource source, AppDatabase database, SessionService sessions, CommandPipeline pipeline, IClock clock,
    ILogger<PortalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Machines' portal read every {Interval} from {Url}.", settings.Interval, settings.BaseUrl);
        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Portal pass failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<Guid> companies;
        await using (var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            companies = await Reading.ListAsync(
                connection,
                null,
                "SELECT DISTINCT company_id FROM mfg.portal_machine ORDER BY company_id",
                r => r.GetGuid(0),
                cancellationToken).ConfigureAwait(false);
        }

        if (companies.Count == 0)
        {
            return 0;
        }

        var session = await sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId, cancellationToken).ConfigureAwait(false);
        var written = 0;
        try
        {
            foreach (var company in companies)
            {
                try
                {
                    var pass = await PortalRunner.RunOnceAsync(
                        pipeline, source, company, session, BusinessCalendar.DefaultBusinessDate(clock.UtcNow), settings.LookbackDays, cancellationToken).ConfigureAwait(false);
                    written += pass.Written;
                    if (!pass.Ok)
                    {
                        logger.LogWarning("Portal read failed for {Company}: {Error}", company, pass.Error);
                    }
                }
                catch (DomainException ex)
                {
                    logger.LogError("Portal pass of {Company} refused: {Code} {Message}", company, ex.Code, ex.Message);
                }
            }
        }
        finally
        {
            await sessions.EndSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        return written;
    }
}

/// <summary>The portal is not configured on this server.</summary>
public sealed class NoPortalSource : IPortalSource
{
    public Task<PortalExport> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
        => throw new HttpRequestException("The machines' portal is not configured on this server (Rochell:Portal:BaseUrl).");
}
