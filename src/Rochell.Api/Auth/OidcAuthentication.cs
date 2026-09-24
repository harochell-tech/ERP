using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Rochell.Api.Hosting;
using Rochell.Api.Http;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;

namespace Rochell.Api.Auth;

/// <summary>
/// E-PR18-2: authorization code with PKCE against Google Workspace, handled entirely by the API. The OIDC handler validates
/// the ID token (signature, issuer, audience, expiry, nonce); <see cref="SessionService"/> then applies the Rochell rules
/// (verified e-mail, hosted domain, active human user with an employee) and opens the iam.session. No ASP.NET identity
/// cookie is issued: the only credential the browser keeps is <see cref="SessionCookie"/>.
/// </summary>
public static class OidcAuthentication
{
    public const string Scheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string CallbackPath = "/api/v1/auth/callback";

    /// <summary>OIDC state item: the protected id of the session being re-authenticated (step-up).</summary>
    private const string StepUpSessionItem = "rochell.step_up_session";

    /// <summary>Transient cookie scheme required by the remote handler; never used, because the callback completes the response itself.</summary>
    private const string UnusedSignInScheme = "rochell.unused";

    public static void AddRochellOidc(this IServiceCollection services, OidcSettings settings, string hostedDomain)
    {
        ArgumentNullException.ThrowIfNull(settings);
        services
            .AddAuthentication(options => options.DefaultChallengeScheme = Scheme)
            .AddCookie(UnusedSignInScheme)
            .AddOpenIdConnect(Scheme, options =>
            {
                options.SignInScheme = UnusedSignInScheme;
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = settings.ClientSecret;
                options.CallbackPath = CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;

                // A top-level GET redirect: the correlation and nonce cookies can stay SameSite=Lax (form_post would need None).
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.SaveTokens = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("email");
                options.Scope.Add("profile");
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        // Google shows only accounts of the organization's domain; the domain is still checked on the token.
                        context.ProtocolMessage.SetParameter("hd", hostedDomain);
                        if (context.Properties.Items.ContainsKey(StepUpSessionItem))
                        {
                            context.ProtocolMessage.Prompt = "login";
                            context.ProtocolMessage.MaxAge = "0";
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = CompleteAsync,
                    OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        return WritePageAsync(context.HttpContext, StatusCodes.Status400BadRequest, "No se pudo completar el inicio de sesión.", null);
                    },
                };
            });
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");

        auth.MapGet("/login", (string? returnUrl) =>
                Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [Scheme]))
            .WithName("Login")
            .WithSummary("Starts the Google Workspace sign-in (browser navigation).")
            .Produces(StatusCodes.Status302Found);

        auth.MapGet("/step-up", (HttpContext http, string? returnUrl, SessionCookie cookie) =>
            {
                var sessionId = cookie.Read(http);
                if (sessionId is null)
                {
                    return ApiProblems.Problem(http, AuthorizationErrors.SessionInvalid, "Sign in first.");
                }

                var properties = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) };
                properties.Items[StepUpSessionItem] = cookie.Protect(sessionId.Value);
                return Results.Challenge(properties, [Scheme]);
            })
            .WithName("StepUp")
            .WithSummary("Re-authenticates the current user (prompt=login) before an action that requires it.")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", async (HttpContext http, SessionCookie cookie, SessionService sessions, CancellationToken cancellationToken) =>
            {
                if (cookie.Read(http) is { } sessionId)
                {
                    await sessions.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }

                cookie.Delete(http);
                return Results.NoContent();
            })
            .WithName("Logout")
            .WithSummary("Ends the session.")
            .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/api/v1/session", async (HttpContext http, SessionCookie cookie, SessionService sessions, ILoggerFactory logs, CancellationToken cancellationToken) =>
            {
                if (cookie.Read(http) is not { } sessionId)
                {
                    return ApiProblems.Problem(http, AuthorizationErrors.SessionInvalid, "Sign in first.");
                }

                try
                {
                    return Results.Json(await sessions.DescribeAsync(sessionId, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ApiProblems.FromException(http, ex, logs.CreateLogger("Rochell.Api.Session"), isQuery: true);
                }
            })
            .WithTags("Auth")
            .WithName("GetSession")
            .WithSummary("The signed-in user, session expiry, step-up freshness and roles per company.")
            .Produces<SessionDescription>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// After the ID token is validated: open the session (login) or record the re-authentication (step-up), set the cookie
    /// and answer with a page that navigates to the return URL. The callback is a cross-site navigation, so the browser would
    /// not send the new SameSite=Strict cookie on an HTTP redirect; a same-site navigation started by the page does send it.
    /// </summary>
    private static async Task CompleteAsync(TokenValidatedContext context)
    {
        var http = context.HttpContext;
        context.HandleResponse();
        var principal = context.Principal ?? throw new InvalidOperationException("The OIDC handler validated no principal.");
        var claims = new OidcClaims(
            principal.FindFirstValue("sub") ?? string.Empty,
            principal.FindFirstValue("email"),
            string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase),
            principal.FindFirstValue("hd"));
        var sessions = http.RequestServices.GetRequiredService<SessionService>();
        var cookie = http.RequestServices.GetRequiredService<SessionCookie>();
        var returnUrl = SafeReturnUrl(context.Properties?.RedirectUri);
        try
        {
            if (context.Properties?.Items.TryGetValue(StepUpSessionItem, out var protectedSession) == true)
            {
                var sessionId = cookie.Unprotect(protectedSession) ?? throw new DomainException(SessionService.LoginRejected, "The re-authentication request is not valid.");
                await sessions.RecordStepUpAsync(sessionId, claims, http.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                var sessionId = await sessions.StartOidcSessionAsync(claims, http.Connection.RemoteIpAddress, UserAgent(http), http.RequestAborted).ConfigureAwait(false);
                cookie.Write(http, sessionId);
            }
        }
        catch (DomainException ex) when (ex.Code == SessionService.LoginRejected)
        {
            await WritePageAsync(http, StatusCodes.Status403Forbidden, "Acceso rechazado: la cuenta no corresponde a un usuario activo de la organización.", null).ConfigureAwait(false);
            return;
        }

        await WritePageAsync(http, StatusCodes.Status200OK, "Sesión iniciada.", returnUrl).ConfigureAwait(false);
    }

    private static async Task WritePageAsync(HttpContext http, int status, string message, string? returnUrl)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        var encoder = HtmlEncoder.Default;
        var refresh = returnUrl is null ? string.Empty : $"<meta http-equiv=\"refresh\" content=\"0;url={encoder.Encode(returnUrl)}\">";
        var link = returnUrl is null ? string.Empty : $"<p><a href=\"{encoder.Encode(returnUrl)}\">Continuar</a></p>";
        await http.Response.WriteAsync(
            $"<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">{refresh}<title>Rochell Core</title></head><body><p>{encoder.Encode(message)}</p>{link}</body></html>",
            http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Only local absolute paths: an open redirect would let a crafted link send a freshly signed-in user elsewhere.</summary>
    public static string SafeReturnUrl(string? returnUrl)
        => returnUrl is { Length: > 0 } url && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            && Uri.IsWellFormedUriString(url, UriKind.Relative)
            ? url
            : "/";

    private static string? UserAgent(HttpContext http)
    {
        var agent = http.Request.Headers.UserAgent.ToString();
        return agent.Length == 0 ? null : agent;
    }
}
