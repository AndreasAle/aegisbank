using AegisBank.Application;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Api.Security;

/// <summary>
/// Converts exceptions into RFC 9457 Problem Details. Expected failures
/// (<see cref="AppException"/>) keep their client-safe message; everything
/// else becomes a generic 500 and is logged with the correlation id. No
/// exception text, stack trace or connection detail reaches the client.
/// </summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case AppException app:
                problem = Problems.Create(StatusFor(app.Kind), app.Code, app.Message);
                if (app.Kind is ErrorKind.Forbidden or ErrorKind.Unauthorized)
                    logger.LogWarning("Request denied: {Code} on {Path}", app.Code, context.Request.Path);
                break;
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                problem = Problems.Create(499, "request_cancelled", "The request was cancelled by the client.");
                break;
            case BadHttpRequestException bad:
                problem = Problems.Create(bad.StatusCode, "bad_request", "The request could not be read.");
                break;
            default:
                logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
                problem = Problems.Create(StatusCodes.Status500InternalServerError, "internal_error",
                    "An unexpected error occurred. Quote the correlation id when reporting it.");
                break;
        }

        context.Response.StatusCode = problem.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}

public static class Problems
{
    public static ProblemDetails Create(int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = Title(status),
            Detail = detail,
            Type = $"https://aegisbank.example/problems/{code.Replace('_', '-')}",
        };
        problem.Extensions["code"] = code;
        return problem;
    }

    /// <summary>Returns the problem as an action result, with the same decoration as middleware-written problems.</summary>
    /// <remarks>
    /// A JsonResult is used on purpose: [Produces("application/json")] on the controllers rewrites the
    /// content type of ObjectResult, which would turn problem responses into plain application/json.
    /// </remarks>
    public static JsonResult Result(HttpContext context, ProblemDetails problem)
    {
        Decorate(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
        return new JsonResult(problem) { StatusCode = problem.Status, ContentType = "application/problem+json" };
    }

    public static void Decorate(ProblemDetailsContext context)
    {
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    }

    private static string Title(int status) => status switch
    {
        400 => "Invalid request",
        401 => "Authentication required",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        422 => "Request could not be processed",
        429 => "Too many requests",
        499 => "Request cancelled",
        503 => "Service temporarily unavailable",
        _ => "Internal error",
    };
}
