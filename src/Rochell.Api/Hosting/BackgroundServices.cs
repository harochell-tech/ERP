using System.Security.Cryptography;
using Rochell.Audit;
using Rochell.Platform.Observability;
using Rochell.Platform.Time;

namespace Rochell.Api.Hosting;

/// <summary>E-PR18-5 / ADR-037: seals pending ledger groups every interval as rochell_sealer, never inside a command.</summary>
public sealed class SealerService(SealerSettings settings, SealerDatabase database, IClock clock, ILogger<SealerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Sealer disabled by configuration.");
            return;
        }

        var sealer = new LedgerSealer(database.DataSource, clock);
        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                var passes = await sealer.SealAllAsync(stoppingToken).ConfigureAwait(false);
                var failed = passes.Sum(p => p.Failed);
                if (failed > 0)
                {
                    logger.LogCritical("Sealer marked {Failed} group(s) SEAL_ERROR: a ledger row no longer matches its hash; closing is blocked until it is resolved (INT-02). Run hash verification.", failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sealing pass failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// E-PR18-5 / E-PR15-5: at <see cref="DigestSettings.RunAt"/> local time (America/Santo_Domingo) digests the previous day to
/// WORM. It does not start without WORM storage (outside TEST, until B-03) or without the signing key; sealing is unaffected.
/// On start it also digests yesterday if that run is due (digesting a day twice changes nothing).
/// </summary>
public sealed class DigestService(DigestSettings settings, SealerDatabase database, WormAccess worm, IClock clock, ILogger<DigestService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Daily digest disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.SigningKeyPem))
        {
            logger.LogCritical("Daily digest NOT started: no signing key configured.");
            return;
        }

        var store = await worm.GetAsync(database.DataSource, stoppingToken).ConfigureAwait(false);
        if (store is null)
        {
            logger.LogCritical("Daily digest NOT started: no WORM storage in this environment (object-lock storage pending, B-03). Sealing continues.");
            return;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(settings.SigningKeyPem);
        var digester = new LedgerDigester(database.DataSource, store, new DigestSigner(key));
        var runAt = settings.RunAt.ToTimeSpan();
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            var today = BusinessCalendar.DefaultBusinessDate(now);
            var dueToday = BusinessCalendar.DayUtcRange(today).StartUtc + runAt;
            if (now >= dueToday)
            {
                try
                {
                    var results = await digester.DigestDayAsync(today.AddDays(-1), stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Digested {Day}: {Created} new digest(s).", today.AddDays(-1), results.Count(r => r.Created));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Digest of {Day} failed; retrying.", today.AddDays(-1));
                    await Task.Delay(RetryAfter, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var next = BusinessCalendar.DayUtcRange(today.AddDays(1)).StartUtc + runAt;
                await Task.Delay(next - clock.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(dueToday - now, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Writes obs.request_log outside the command transactions, every second and once more at shutdown (Errata E-3).</summary>
public sealed class RequestLogFlusher(RequestLogWriter writer) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await writer.FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the final flush below still runs.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
