namespace Rochell.Api.Http;

/// <summary>
/// E-PR18-2 anti-CSRF: every state-changing request must carry a custom header. A cross-site form cannot set it, and a
/// cross-site script cannot send it without a CORS preflight, which this API never grants. Complements SameSite=Strict.
/// </summary>
public static class CsrfHeader
{
    public const string Name = "X-Rochell-Csrf";
    public const string Value = "1";

    public static IApplicationBuilder UseCsrfHeader(this IApplicationBuilder app)
        => app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
                && !HttpMethods.IsGet(http.Request.Method)
                && !HttpMethods.IsHead(http.Request.Method)
                && !HttpMethods.IsOptions(http.Request.Method)
                && http.Request.Headers[Name] != Value)
            {
                await ApiProblems.Problem(http, ApiErrors.CsrfHeaderRequired, $"State-changing requests must send the header {Name}: {Value}.")
                    .ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            await next(http).ConfigureAwait(false);
        });
}
