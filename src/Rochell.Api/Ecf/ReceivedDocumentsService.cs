using System.Text.Json;
using Rochell.Api.Hosting;
using Rochell.Identity;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Procurement.SupplierDocuments;
using Rochell.Tax.Ecf;

namespace Rochell.Api.Ecf;

/// <summary>E-OCR1-02-7: Alanube's webhook brings the next reading of received documents forward.</summary>
public sealed class ReceptionNudge
{
    private int _pending;

    public void Nudge() => Interlocked.Exchange(ref _pending, 1);

    public bool Take() => Interlocked.Exchange(ref _pending, 0) == 1;
}

/// <summary>
/// OCR1-02 (E-OCR1-01-9, E-OCR1-02-1/3/7): every <see cref="EcfSettings.Interval"/>, per company, as the daily process (PROCESO_DIARIO): sends
/// the commercial responses kept and not yet taken by Alanube, and — every <see cref="EcfSettings.ReceptionInterval"/>, or at once after
/// a webhook — reads the received documents, again while a reading says there is more.
/// </summary>
public sealed class ReceivedDocumentsService(
    EcfSettings settings, AppDatabase database, SessionService sessions, CommandPipeline pipeline, IServiceProvider services, ReceptionNudge nudge,
    ILogger<ReceivedDocumentsService> logger) : BackgroundService
{
    /// <summary>Readings in a row while Alanube has more.</summary>
    private const int MaxReadings = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Received documents pass failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass; returns the readings and sends run.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var nudged = nudge.Take();
        var work = new List<(Guid Company, bool Read, List<Guid> Unsent)>();
        await using (var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var companies = await Reading.ListAsync(connection, null, "SELECT company_id FROM md.company ORDER BY company_id", r => r.GetGuid(0), cancellationToken).ConfigureAwait(false);
            foreach (var company in companies)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @c, true)", cancellationToken, ("c", company.ToString())).ConfigureAwait(false);
                var lastAttempt = (await Reading.ListAsync(
                    connection, transaction, "SELECT last_attempt_at FROM pur.received_document_sync WHERE company_id = @c", r => (DateTime?)r.Utc(0), cancellationToken, ("c", company))
                    .ConfigureAwait(false)).SingleOrDefault();
                var unsent = await Reading.ListAsync(
                    connection,
                    transaction,
                    """
                    SELECT supplier_document_id FROM pur.supplier_document
                    WHERE company_id = @c AND commercial_response <> 'NOT_DECLARED' AND response_sent_at IS NULL AND provider_id IS NOT NULL
                    ORDER BY responded_at LIMIT 50
                    """,
                    r => r.GetGuid(0),
                    cancellationToken,
                    ("c", company)).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var read = nudged || lastAttempt is null || DateTime.UtcNow - lastAttempt.Value >= settings.ReceptionInterval;
                if (read || unsent.Count > 0)
                {
                    work.Add((company, read, unsent));
                }
            }
        }

        if (work.Count == 0)
        {
            return 0;
        }

        var session = await sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId, cancellationToken).ConfigureAwait(false);
        var runs = 0;
        try
        {
            foreach (var (company, read, unsent) in work)
            {
                try
                {
                    if (read)
                    {
                        for (var i = 0; i < MaxReadings; i++)
                        {
                            var result = await pipeline.ExecuteAsync(
                                new ImportReceivedDocuments(company, session, $"received:{Guid.CreateVersion7()}"), services.GetRequiredService<ImportReceivedDocumentsHandler>(),
                                Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
                            runs++;
                            using var outcome = JsonDocument.Parse(result.ResultPayload);
                            if (!outcome.RootElement.GetProperty("more").GetBoolean() || outcome.RootElement.GetProperty("error").ValueKind == JsonValueKind.String)
                            {
                                break;
                            }
                        }
                    }

                    foreach (var document in unsent)
                    {
                        await pipeline.ExecuteAsync(
                            new SendSupplierDocumentResponse(company, session, $"response:{document}:{Guid.CreateVersion7()}", document),
                            services.GetRequiredService<SendSupplierDocumentResponseHandler>(), Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
                        runs++;
                    }
                }
                catch (DomainException ex) when (ex.Code == AuthorizationErrors.NotAuthorized)
                {
                    logger.LogWarning("Company {Company} has no PROCESO_DIARIO assignment; its received e-CF are not read.", company);
                }
                catch (DomainException ex)
                {
                    logger.LogError("Received documents of {Company} refused: {Code} {Message}", company, ex.Code, ex.Message);
                }
            }
        }
        finally
        {
            await sessions.EndSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        return runs;
    }
}
