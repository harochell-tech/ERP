using Microsoft.Extensions.FileProviders;

namespace Rochell.Api.Http;

/// <summary>
/// E-PR18b-2: the web UI is a static export served by this host, so the browser talks to one origin (no CORS, the SameSite=Strict
/// session cookie works as is). Every page gets a restrictive Content-Security-Policy; the export's inline bootstrap scripts
/// need 'unsafe-inline' for scripts, everything else is limited to this origin and the page can never be framed.
/// </summary>
public static class WebAssets
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public static IApplicationBuilder UseWebAssets(this IApplicationBuilder app, string webRoot)
    {
        var root = Path.GetFullPath(webRoot);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"Rochell:WebRoot '{root}' does not exist; build web/ first (npm run build).");
        }

        var files = new PhysicalFileProvider(root);
        app.Use(async (http, next) =>
        {
            if (!IsApi(http.Request.Path))
            {
                http.Response.OnStarting(() =>
                {
                    var headers = http.Response.Headers;
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
                    headers.XContentTypeOptions = "nosniff";
                    headers["Referrer-Policy"] = "same-origin";
                    if (http.Response.ContentType?.StartsWith("text/html", StringComparison.Ordinal) == true)
                    {
                        headers.CacheControl = "no-cache";
                    }

                    return Task.CompletedTask;
                });
            }

            await next(http).ConfigureAwait(false);
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        // Unknown page paths get the export's 404 page; /api and /openapi keep their own answers.
        app.Use(async (http, next) =>
        {
            await next(http).ConfigureAwait(false);
            if (http.Response.StatusCode == StatusCodes.Status404NotFound && !http.Response.HasStarted && !IsApi(http.Request.Path)
                && HttpMethods.IsGet(http.Request.Method) && files.GetFileInfo("404.html") is { Exists: true } notFound)
            {
                http.Response.ContentType = "text/html; charset=utf-8";
                await http.Response.SendFileAsync(notFound).ConfigureAwait(false);
            }
        });
        return app;
    }

    private static bool IsApi(PathString path)
        => path.StartsWithSegments("/api", StringComparison.Ordinal) || path.StartsWithSegments("/openapi", StringComparison.Ordinal);
}
