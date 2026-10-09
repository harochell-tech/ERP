using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rochell.Api.Hosting;
using Rochell.Identity;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Tax.Ecf;

namespace Rochell.Api.Ecf;

/// <summary>
/// E-VS4-02-5: Alanube's webhook. Only a request carrying our secret in <see cref="EcfModes.WebhookHeader"/> counts; its content is
/// never believed — at most it names the e-CF (Alanube's id) whose status query is moved forward, else every e-CF in flight is; the next
/// reading of received documents is brought forward too (E-OCR1-02-7). It changes nothing by itself. Exempt from the anti-CSRF header (it has its own secret, and no session).
/// </summary>
public static class EcfWebhook
{
    public const string Path = "/api/v1/ecf/webhook";
    private const int MaxBody = 65536; // type-limit: a notice, not a document

    public static void MapEcfWebhook(this WebApplication app)
        => app.MapPost(Path, HandleAsync)
            .WithTags("Ecf")
            .WithName("EcfWebhook")
            .WithSummary("E-VS4-02-5: Alanube's notice that an e-CF finished; it only moves forward the status query. Needs the secret header.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(
        HttpContext http, EcfSettings settings, AppDatabase database, SessionService sessions, CommandPipeline pipeline, NudgeEcfDocumentsHandler handler, ReceptionNudge reception,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("Rochell.Api.Ecf.EcfWebhook");
        if (!settings.Enabled || string.IsNullOrEmpty(settings.WebhookSecret))
        {
            return Results.NotFound();
        }

        var given = Encoding.UTF8.GetBytes(http.Request.Headers[EcfModes.WebhookHeader].ToString());
        var expected = Encoding.UTF8.GetBytes(settings.WebhookSecret);
        if (!CryptographicOperations.FixedTimeEquals(given, expected))
        {
            logger.LogWarning("e-CF webhook refused from {Address}: missing or wrong secret header.", http.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }

        // E-OCR1-02-7: a notice may also be of a supplier's e-CF: the next reading of received documents comes at once.
        reception.Nudge();
        var providerId = await ProviderIdAsync(http.Request, cancellationToken).ConfigureAwait(false);
        List<Guid> companies;
        await using (var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            companies = await Reading.ListAsync(connection, null, "SELECT company_id FROM md.company ORDER BY company_id", r => r.GetGuid(0), cancellationToken).ConfigureAwait(false);
        }

        var session = await sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var company in companies)
            {
                try
                {
                    await pipeline.ExecuteAsync(new NudgeEcfDocuments(company, session, $"ecf-webhook:{Guid.CreateVersion7()}", providerId), handler, Guid.CreateVersion7(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DomainException ex) when (ex.Code == AuthorizationErrors.NotAuthorized)
                {
                    logger.LogWarning("Company {Company} has no PROCESO_DIARIO assignment; the e-CF webhook was not applied to it.", company);
                }
            }
        }
        finally
        {
            await sessions.EndSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        return Results.Accepted();
    }

    /// <summary>The Alanube id the notice names, if its (undocumented) body has one at <c>id</c> or <c>document.id</c>.</summary>
    private static async Task<string?> ProviderIdAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxBody)
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }

            return root.TryGetProperty("document", out var inner) && inner.ValueKind == JsonValueKind.Object && inner.TryGetProperty("id", out var innerId)
                   && innerId.ValueKind == JsonValueKind.String
                ? innerId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
