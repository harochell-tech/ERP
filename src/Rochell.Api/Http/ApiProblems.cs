using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Queries;

namespace Rochell.Api.Http;

/// <summary>Error codes the transport adds to the domain ones.</summary>
public static class ApiErrors
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string CsrfHeaderRequired = "CSRF_HEADER_REQUIRED";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>
/// E-PR18-3: RFC 9457 problem details carrying the domain code. No session 401; no permission or step-up needed 403 (the UI
/// re-authenticates on STEP_UP_REQUIRED); version change or conflict 409; any other business rule 422. On the read side a
/// missing document is 404 and a bad filter 400.
/// </summary>
public static class ApiProblems
{
    public const string ContentType = "application/problem+json";

    public static int StatusOf(string code, bool isQuery) => code switch
    {
        AuthorizationErrors.SessionInvalid or AuthorizationErrors.SessionExpired => StatusCodes.Status401Unauthorized,
        AuthorizationErrors.NotAuthorized or AuthorizationErrors.StepUpRequired or ApiErrors.CsrfHeaderRequired => StatusCodes.Status403Forbidden,
        "VERSION_CONFLICT" or ApiErrors.ConcurrencyConflict => StatusCodes.Status409Conflict,
        QueryErrors.NotFound when isQuery => StatusCodes.Status404NotFound,
        QueryErrors.InvalidParameter when isQuery => StatusCodes.Status400BadRequest,
        ApiErrors.InvalidRequest or ApiErrors.IdempotencyKeyRequired => StatusCodes.Status400BadRequest,
        ApiErrors.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        ApiErrors.InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    public static IResult Problem(HttpContext http, string code, string detail, bool isQuery = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        var status = StatusOf(code, isQuery);
        var problem = new ProblemDetails
        {
            Type = "urn:rochell:error:" + code,
            Title = code,
            Status = status,
            Detail = detail,
            Instance = http.Request.Path,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = Correlation.Of(http).ToString();
        return Results.Problem(problem);
    }

    /// <summary>Maps what a pipeline threw. Anything unexpected is logged and reported without internals.</summary>
    public static IResult FromException(HttpContext http, Exception exception, ILogger logger, bool isQuery)
    {
        ArgumentNullException.ThrowIfNull(logger);
        switch (exception)
        {
            case DomainException domain:
                return Problem(http, domain.Code, domain.Message, isQuery);
            case DbException db when SqlStates.IsRetryable(db.SqlState):
                return Problem(http, ApiErrors.ConcurrencyConflict, "The request conflicted with concurrent changes; retry with the same Idempotency-Key.", isQuery);
            case ServiceUnavailableException unavailable:
                return Problem(http, ApiErrors.ServiceUnavailable, unavailable.Message, isQuery);
            default:
                logger.LogError(exception, "Request {Path} failed (correlation {CorrelationId}).", http.Request.Path, Correlation.Of(http));
                return Problem(http, ApiErrors.InternalError, "Unexpected error; report the correlation id.", isQuery);
        }
    }
}

/// <summary>A dependency of the request (e.g. WORM storage) is not configured in this environment.</summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException()
    {
    }

    public ServiceUnavailableException(string message)
        : base(message)
    {
    }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
