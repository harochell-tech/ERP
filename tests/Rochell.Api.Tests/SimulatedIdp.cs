using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Rochell.Identity.Sessions;

namespace Rochell.Api.Tests;

/// <summary>
/// E-PR18-2 / A-03: a simulated Google Workspace for CI. It exists only under tests/. The API's real OpenID Connect handler
/// talks to it: discovery, JWKS and the token endpoint are answered through <see cref="Backchannel"/>; the browser leg
/// (the authorization endpoint) is <see cref="Authorize"/>, which a test calls with the redirect the API produced.
/// ID tokens are RS256-signed with a key generated per instance and carry sub, email, email_verified, hd and the nonce.
/// </summary>
public sealed class SimulatedIdp : IDisposable
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "rochell-api-test";
    public const string ClientSecret = "simulated-client-secret";
    private const string KeyId = "simulated-key-1";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, OidcClaims> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);

    public SimulatedIdp()
    {
        Backchannel = new BackchannelHandler(this);
    }

    /// <summary>The OIDC handler's backchannel (metadata, keys, code redemption).</summary>
    public HttpMessageHandler Backchannel { get; }

    /// <summary>Query parameters of the last authorization request (e.g. prompt=login on a step-up).</summary>
    public IReadOnlyDictionary<string, string> LastAuthorizeRequest { get; private set; } = new Dictionary<string, string>();

    public void AddAccount(OidcClaims account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _accounts[account.Subject] = account;
    }

    /// <summary>
    /// The user signs in at the IdP: validates the request, issues a one-time code and returns the redirect back to the API
    /// (redirect_uri?code=…&amp;state=…), as the browser would follow it.
    /// </summary>
    public Uri Authorize(Uri authorizeRequest, string subject)
    {
        ArgumentNullException.ThrowIfNull(authorizeRequest);
        var query = QueryHelpers.ParseQuery(authorizeRequest.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        LastAuthorizeRequest = query;
        if (authorizeRequest.GetLeftPart(UriPartial.Path) != Issuer + "/authorize"
            || query.GetValueOrDefault("client_id") != ClientId
            || query.GetValueOrDefault("response_type") != "code"
            || query.GetValueOrDefault("code_challenge_method") != "S256"
            || !query.ContainsKey("code_challenge") || !query.ContainsKey("nonce") || !query.ContainsKey("state") || !query.ContainsKey("redirect_uri"))
        {
            throw new InvalidOperationException("Unexpected authorization request: " + authorizeRequest);
        }

        if (!_accounts.TryGetValue(subject, out var account))
        {
            throw new InvalidOperationException($"No simulated account {subject}.");
        }

        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new PendingCode(account, query["nonce"], query["redirect_uri"], query["code_challenge"]);
        return new Uri(QueryHelpers.AddQueryString(query["redirect_uri"], new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
    }

    public void Dispose()
    {
        _rsa.Dispose();
        Backchannel.Dispose();
    }

    private string IdToken(PendingCode pending)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = pending.Account.Subject,
            ["email_verified"] = pending.Account.EmailVerified,
            ["nonce"] = pending.Nonce,
            ["auth_time"] = EpochTime.GetIntDate(now),
        };
        if (pending.Account.Email is not null)
        {
            claims["email"] = pending.Account.Email;
        }

        if (pending.Account.HostedDomain is not null)
        {
            claims["hd"] = pending.Account.HostedDomain;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    private HttpResponseMessage Token(Dictionary<string, string> form)
    {
        if (form.GetValueOrDefault("grant_type") != "authorization_code"
            || form.GetValueOrDefault("client_id") != ClientId
            || form.GetValueOrDefault("client_secret") != ClientSecret
            || !_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var pending)
            || form.GetValueOrDefault("redirect_uri") != pending.RedirectUri
            || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? string.Empty))) != pending.CodeChallenge)
        {
            return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
        }

        return Json(HttpStatusCode.OK, new { access_token = "simulated-access-token", token_type = "Bearer", expires_in = 300, id_token = IdToken(pending) });
    }

    private object Discovery() => new Dictionary<string, object>
    {
        ["issuer"] = Issuer,
        ["authorization_endpoint"] = Issuer + "/authorize",
        ["token_endpoint"] = Issuer + "/token",
        ["jwks_uri"] = Issuer + "/jwks",
        ["response_types_supported"] = new[] { "code" },
        ["subject_types_supported"] = new[] { "public" },
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["code_challenge_methods_supported"] = new[] { "S256" },
        ["scopes_supported"] = new[] { "openid", "email", "profile" },
    };

    private object Jwks()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        return new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) },
            },
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private sealed record PendingCode(OidcClaims Account, string Nonce, string RedirectUri, string CodeChallenge);

    private sealed class BackchannelHandler(SimulatedIdp idp) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (request.Method == HttpMethod.Get && path == Issuer + "/.well-known/openid-configuration")
            {
                return Json(HttpStatusCode.OK, idp.Discovery());
            }

            if (request.Method == HttpMethod.Get && path == Issuer + "/jwks")
            {
                return Json(HttpStatusCode.OK, idp.Jwks());
            }

            if (request.Method == HttpMethod.Post && path == Issuer + "/token")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return idp.Token(QueryHelpers.ParseQuery(body).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
