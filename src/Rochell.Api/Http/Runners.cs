using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Api.Auth;
using Rochell.Platform.Commands;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Api.Http;

/// <summary>What every command endpoint returns: the command, the resource it created (P-3) and the handler's result.</summary>
public sealed record CommandResponse(Guid CommandId, Guid ResultRef, bool Replayed, JsonElement Result);

/// <summary>
/// E-PR18-3 command transport: tenant from the route, session from the cookie, idempotency key from the header, the rest
/// from the body; then <see cref="CommandPipeline"/> does everything else (authorization, idempotency, transaction, log).
/// </summary>
public sealed class CommandRunner(CommandPipeline pipeline, SessionCookie cookie, ILogger<CommandRunner> logger)
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";
    private const int MaxIdempotencyKeyLength = 200;

    /// <summary>Filled by the server; a body that sets them is rejected rather than silently overwritten.</summary>
    public static readonly string[] ServerFields = ["companyId", "sessionId", "idempotencyKey"];

    public async Task<IResult> RunAsync<TCommand>(HttpContext http, Guid companyId, ICommandHandler<TCommand> handler, CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(http);
        if (cookie.Read(http) is not { } sessionId)
        {
            return ApiProblems.Problem(http, AuthorizationErrors.SessionInvalid, "Sign in first.");
        }

        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (key.Length is 0 or > MaxIdempotencyKeyLength)
        {
            return ApiProblems.Problem(http, ApiErrors.IdempotencyKeyRequired, $"Send a unique {IdempotencyKeyHeader} header (1 to {MaxIdempotencyKeyLength} characters) per intent.");
        }

        TCommand command;
        try
        {
            command = await BindAsync<TCommand>(http, companyId, sessionId, key, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return ApiProblems.Problem(http, ApiErrors.InvalidRequest, "Invalid request body: " + ex.Message);
        }

        try
        {
            var result = await pipeline.ExecuteAsync(command, handler, Correlation.Of(http), cancellationToken).ConfigureAwait(false);
            if (result.Duplicate)
            {
                http.Response.Headers[ReplayedHeader] = "true";
            }

            using var payload = JsonDocument.Parse(result.ResultPayload);
            return Results.Json(new CommandResponse(result.CommandId, result.ResultRef, result.Duplicate, payload.RootElement.Clone()), ApiJson.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApiProblems.FromException(http, ex, logger, isQuery: false);
        }
    }

    private static async Task<TCommand> BindAsync<TCommand>(HttpContext http, Guid companyId, Guid sessionId, string key, CancellationToken cancellationToken)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new JsonException("The body must be a JSON object.");
        foreach (var field in ServerFields)
        {
            if (body.Any(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)))
            {
                throw new JsonException($"'{field}' is set by the server (route, session cookie, {IdempotencyKeyHeader} header).");
            }
        }

        body["companyId"] = companyId;
        body["sessionId"] = sessionId;
        body["idempotencyKey"] = key;
        return body.Deserialize<TCommand>(ApiJson.Options) ?? throw new JsonException("The body must be a JSON object.");
    }
}

/// <summary>Read side: session from the cookie, then <see cref="QueryPipeline"/> (authorization, RLS, READ ONLY).</summary>
public sealed class QueryRunner(QueryPipeline pipeline, SessionCookie cookie, ILogger<QueryRunner> logger)
{
    public async Task<IResult> RunAsync<TQuery>(HttpContext http, Func<Guid, TQuery> query, IQueryHandler<TQuery> handler, CancellationToken cancellationToken)
        where TQuery : IQuery
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(query);
        if (cookie.Read(http) is not { } sessionId)
        {
            return ApiProblems.Problem(http, AuthorizationErrors.SessionInvalid, "Sign in first.", isQuery: true);
        }

        try
        {
            var json = await pipeline.ExecuteAsync(query(sessionId), handler, cancellationToken).ConfigureAwait(false);
            return Results.Text(json, "application/json; charset=utf-8");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApiProblems.FromException(http, ex, logger, isQuery: true);
        }
    }
}
