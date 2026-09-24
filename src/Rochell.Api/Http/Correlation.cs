using Rochell.Platform.Ids;

namespace Rochell.Api.Http;

/// <summary>
/// One correlation id per request (propagated to command_log, events and request_log). A caller may supply a UUID in
/// <see cref="Header"/>; otherwise one is generated. It is echoed in the response.
/// </summary>
public static class Correlation
{
    public const string Header = "X-Correlation-Id";
    private static readonly object Key = new();

    public static Guid Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Items.TryGetValue(Key, out var value) && value is Guid id)
        {
            return id;
        }

        id = Guid.TryParse(http.Request.Headers[Header].ToString(), out var supplied) && supplied != Guid.Empty ? supplied : UuidV7Generator.Instance.NewId();
        http.Items[Key] = id;
        return id;
    }

    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app)
        => app.Use(async (http, next) =>
        {
            var id = Of(http);
            http.Response.OnStarting(() =>
            {
                http.Response.Headers[Header] = id.ToString();
                return Task.CompletedTask;
            });
            await next(http).ConfigureAwait(false);
        });
}
