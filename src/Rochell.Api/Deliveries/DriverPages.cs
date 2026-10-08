using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using Rochell.Api.Hosting;
using Rochell.Api.Http;
using Rochell.Identity;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Files;
using Rochell.Platform.Queries;
using Rochell.Sales.Deliveries;

namespace Rochell.Api.Deliveries;

/// <summary>
/// ENT1-02 (E-ENT-1…6, E-ENT1-01-2/5/8): the drivers' page has no sign-in. The link names the company, the delivery, its generation
/// and the HMAC; the API acts as the service identity «Confirmación de entrega» (role CONFIRMACION_ENTREGA, delivery:driver_confirm
/// only). The photo is checked (JPEG / PNG, ≤ 5 MB) and stored in the private evidence bucket only after the PIN is right.
/// </summary>
public static class DriverPages
{
    public const string Prefix = "/api/v1/public/deliveries";

    public static void MapDriverPages(this WebApplication app)
    {
        var group = app.MapGroup(Prefix).WithTags("DriverPages");
        group.MapGet("/{companyId:guid}/{deliveryId:guid}", ViewAsync)
            .WithName("GetDriverDeliveryPublic")
            .WithSummary("E-ENT-1: what the driver's page shows of a delivery (no prices) and the link's state. No sign-in; the link's HMAC is the key.")
            .Produces<DriverDeliveryView>()
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{companyId:guid}/{deliveryId:guid}/pin", PinAsync)
            .WithName("VerifyDriverPinPublic")
            .WithSummary("E-ENT-2: checks the driver's PIN before the photo. Every try is kept; 5 wrong lock the link.")
            .Produces<DriverAccessResult>()
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{companyId:guid}/{deliveryId:guid}/confirm", ConfirmAsync)
            .WithName("ConfirmDeliveryPublic")
            .WithSummary("E-ENT-3/4: the driver's confirmation (multipart, with the photo or signature). FULL records the POD; DIFFERENCES waits for Dispatch.")
            .DisableAntiforgery()
            .Produces<DriverAccessResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status404NotFound);
    }

    public sealed record PinRequest(int Generation, string Mac, string Pin);

    /// <summary>The page's answer: OK, WRONG_PIN, THROTTLED, INVALID, EXPIRED, LOCKED, CONFIRMED, ANNULLED, NO_PIN, DELIVERED, DIFFERENCES_REPORTED.</summary>
    public sealed record DriverAccessResult(string State, int Remaining);

    private static async Task<IResult> ViewAsync(
        Guid companyId, Guid deliveryId, int g, string k, [FromServices] DriverLinkKey? key, SessionService sessions, QueryPipeline queries, GetDriverDeliveryHandler handler, CancellationToken ct)
    {
        if (key is null)
        {
            return Results.NotFound();
        }

        return await AsServiceAsync(sessions, async session =>
        {
            var json = await queries.ExecuteAsync(new GetDriverDelivery(companyId, session, deliveryId, g, k), handler, ct).ConfigureAwait(false);
            return Results.Content(json, "application/json");
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> PinAsync(
        HttpContext http, Guid companyId, Guid deliveryId, PinRequest body, [FromServices] DriverLinkKey? key, SessionService sessions, CommandPipeline pipeline, VerifyDriverPinHandler handler,
        CancellationToken ct)
    {
        if (key is null || body is null)
        {
            return Results.NotFound();
        }

        var access = new DriverLinkAccess(deliveryId, body.Generation, body.Mac ?? string.Empty, body.Pin ?? string.Empty, Address(http));
        return await AsServiceAsync(sessions, async session =>
        {
            var result = await pipeline.ExecuteAsync(new VerifyDriverPin(companyId, session, $"pin:{Guid.CreateVersion7()}", access), handler, Guid.CreateVersion7(), ct)
                .ConfigureAwait(false);
            return Results.Content(result.ResultPayload, "application/json");
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> ConfirmAsync(
        HttpContext http, Guid companyId, Guid deliveryId, [FromServices] DriverLinkKey? key, [FromServices] IEvidenceStore? store, SessionService sessions, CommandPipeline pipeline,
        VerifyDriverPinHandler verify, ConfirmDeliveryByDriverHandler confirm, CancellationToken ct)
    {
        if (key is null || store is null)
        {
            return Results.NotFound();
        }

        // The page creates the key when the form opens and resends it after a lost signal (E-ENT-6): the same confirmation once.
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length is < 8 or > 100 || !http.Request.HasFormContentType)
        {
            return ApiProblems.Problem(http, DriverConfirmationErrors.ConfirmationInvalid, "The confirmation needs its Idempotency-Key and a multipart form.");
        }

        var form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
        var file = form.Files.GetFile("evidence");
        if (file is null || file.Length is 0 or > EvidenceImages.MaxBytes)
        {
            return ApiProblems.Problem(http, DriverConfirmationErrors.ConfirmationInvalid, "The photo or signature is required (JPEG or PNG, at most 5 MB).");
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            content = buffer.ToArray();
        }

        var contentType = EvidenceImages.ContentType(content);
        if (contentType is null)
        {
            return ApiProblems.Problem(http, DriverConfirmationErrors.ConfirmationInvalid, "The photo or signature must be a JPEG or PNG image.");
        }

        if (!int.TryParse(form["generation"], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            return Results.NotFound();
        }

        var access = new DriverLinkAccess(deliveryId, generation, form["mac"].ToString(), form["pin"].ToString(), Address(http));
        DriverReport report;
        try
        {
            report = new DriverReport(
                form["receiverName"].ToString(), Optional(form["receiverNationalId"]), form["outcome"].ToString(), Optional(form["note"]), Time(form["phoneAt"]),
                Number(form["latitude"]), Number(form["longitude"]), Number(form["accuracy"]), form["evidenceKind"].ToString(), string.Empty, string.Empty);
        }
        catch (FormatException)
        {
            return ApiProblems.Problem(http, DriverConfirmationErrors.ConfirmationInvalid, "The time or the location is not valid.");
        }

        return await AsServiceAsync(sessions, async session =>
        {
            try
            {
                // The PIN first (the try is kept), then the photo, then the confirmation, which checks the PIN again.
                var checkedPin = JsonDocument.Parse((await pipeline.ExecuteAsync(
                    new VerifyDriverPin(companyId, session, $"{idempotencyKey}:pin:{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(access.Pin)))[..12]}", access), verify, Guid.CreateVersion7(), ct).ConfigureAwait(false)).ResultPayload).RootElement;
                if (checkedPin.GetProperty("state").GetString() != "OK")
                {
                    return Results.Content(checkedPin.GetRawText(), "application/json");
                }

                var evidenceRef = string.Create(CultureInfo.InvariantCulture, $"entregas/{companyId:N}/{deliveryId:N}/{Guid.CreateVersion7():N}.{EvidenceImages.Extension(contentType)}");
                await store.PutAsync(evidenceRef, content, contentType, ct).ConfigureAwait(false);
                var result = await pipeline.ExecuteAsync(
                    new ConfirmDeliveryByDriver(companyId, session, idempotencyKey, access, report with { EvidenceRef = evidenceRef, EvidenceSha256 = Convert.ToHexStringLower(SHA256.HashData(content)) }),
                    confirm, Guid.CreateVersion7(), ct).ConfigureAwait(false);
                return Results.Content(result.ResultPayload, "application/json");
            }
            catch (DomainException ex) when (ex.Code is DriverConfirmationErrors.ConfirmationInvalid or DeliveryErrors.EvidenceInvalid)
            {
                return ApiProblems.Problem(http, ex.Code, ex.Message);
            }
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> AsServiceAsync(SessionService sessions, Func<Guid, Task<IResult>> action)
    {
        var session = await sessions.StartServiceSessionAsync(IdentityConstants.DeliveryConfirmationUserId).ConfigureAwait(false);
        try
        {
            return await action(session).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Code is AuthorizationErrors.NotAuthorized or QueryErrors.NotFound or DriverConfirmationErrors.LinkInvalid)
        {
            return Results.NotFound(); // the company does not exist or does not use the drivers' page
        }
        finally
        {
            await sessions.EndSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string Address(HttpContext http) => (http.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback).ToString();

    private static string? Optional(Microsoft.Extensions.Primitives.StringValues value) => string.IsNullOrWhiteSpace(value) ? null : value.ToString();

    private static decimal? Number(Microsoft.Extensions.Primitives.StringValues value)
        => string.IsNullOrWhiteSpace(value) ? null : decimal.Parse(value.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static DateTime? Time(Microsoft.Extensions.Primitives.StringValues value)
        => string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

/// <summary>E-ENT-5: the private evidence bucket in Backblaze B2 (S3 API), apart from the WORM one, with its own key limited to it.</summary>
public sealed class S3EvidenceStore(IAmazonS3 s3, string bucket) : IEvidenceStore
{
    public static S3EvidenceStore Create(EvidenceS3Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var config = new AmazonS3Config
        {
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            config.ServiceURL = settings.ServiceUrl;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = settings.Region;
        }
        else if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }

        var id = File.ReadAllText(settings.AccessKeyIdFile!).Trim();
        var secret = File.ReadAllText(settings.SecretAccessKeyFile!).Trim();
        return new S3EvidenceStore(new AmazonS3Client(new BasicAWSCredentials(id, secret), config), settings.Bucket!);
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(content);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = body, ContentType = contentType }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EvidenceFile?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return new EvidenceFile(buffer.ToArray(), response.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

/// <summary>Evidence in a local folder: Development and Test only (the dev stack, the API tests).</summary>
public sealed class FileSystemEvidenceStore(string root) : IEvidenceStore
{
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EvidenceFile?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        return File.Exists(path)
            ? new EvidenceFile(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), key.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg")
            : null;
    }

    private string PathOf(string key)
    {
        var full = Path.GetFullPath(Path.Combine(root, key));
        return full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) ? full : throw new ArgumentException("The evidence key leaves the store.", nameof(key));
    }
}

/// <summary>ENT1-02: the drivers' page. Off unless the link key is configured; then the evidence store is required.</summary>
public sealed class DeliveriesSettings
{
    /// <summary>E-ENT1-01-2: file with the link key (32 random bytes, base64), generated on the server.</summary>
    public string? LinkKeyFile { get; set; }

    /// <summary>The key itself, base64 (Development and Test).</summary>
    public string? LinkKey { get; set; }

    /// <summary>Folder for the evidence (Development and Test only).</summary>
    public string? EvidenceRoot { get; set; }

    public EvidenceS3Settings Evidence { get; set; } = new();
}

/// <summary>E-ENT-5: the private evidence bucket; its key id and secret are files the owner writes on the server.</summary>
public sealed class EvidenceS3Settings
{
    public string? Bucket { get; set; }

    public string? Region { get; set; }

    public string? ServiceUrl { get; set; }

    public string? AccessKeyIdFile { get; set; }

    public string? SecretAccessKeyFile { get; set; }
}
