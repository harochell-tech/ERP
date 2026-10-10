using Rochell.Api.Hosting;
using Rochell.Identity;
using Rochell.Identity.Sessions;
using Rochell.Manufacturing.Quality;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;

namespace Rochell.Api.Quality;

/// <summary>
/// LAB1-03c (E-LAB1-03-8, 15): the page a lab certificate's QR opens has no sign-in. The link names the company and the certificate's
/// public code; the API answers as the service identity «Verificación pública» (role VERIFICACION_PUBLICA, lab_certificate:verify only).
/// An unknown company or code is a 404 — the page says it cannot verify it.
/// </summary>
public static class CertificatePages
{
    public const string Prefix = "/api/v1/public/lab-certificates";

    public static void MapCertificatePages(this WebApplication app)
    {
        app.MapGroup(Prefix).WithTags("CertificatePages")
            .MapGet("/{companyId:guid}/{code}", VerifyAsync)
            .WithName("VerifyLabCertificatePublic")
            .WithSummary("E-LAB1-03-8: what the certificate's public page shows — number, product, lot, break date, specimens, average, minimum, CV, issue date and status. No customer, no site.")
            .Produces<LabCertificateVerification>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> VerifyAsync(Guid companyId, string code, SessionService sessions, QueryPipeline queries, VerifyLabCertificateHandler handler, CancellationToken ct)
    {
        var session = await sessions.StartServiceSessionAsync(IdentityConstants.PublicVerificationUserId).ConfigureAwait(false);
        try
        {
            var json = await queries.ExecuteAsync(new VerifyLabCertificate(companyId, session, code), handler, ct).ConfigureAwait(false);
            return Results.Content(json, "application/json");
        }
        catch (DomainException ex) when (ex.Code is AuthorizationErrors.NotAuthorized or QueryErrors.NotFound)
        {
            return Results.NotFound();
        }
        finally
        {
            await sessions.EndSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
