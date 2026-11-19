using Content.Store;
using Microsoft.AspNetCore.Diagnostics;
using MongoDB.Driver;

namespace Content.Api.Infrastructure;

/// <summary>
/// Turns exceptions into RFC 7807 problem responses. Domain errors keep their message; anything unexpected is
/// logged in full and answered with a generic message, so no stack trace or internal detail leaves the process.
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        var (status, title, detail) = exception switch
        {
            UnknownSupplierException e => (StatusCodes.Status404NotFound, "Unknown supplier", e.Message),
            InvalidCursorException e => (StatusCodes.Status400BadRequest, "Invalid cursor", e.Message),
            InvalidQueryException e => (StatusCodes.Status400BadRequest, "Invalid request", e.Message),
            MongoExecutionTimeoutException or MongoConnectionException or TimeoutException =>
                (StatusCodes.Status503ServiceUnavailable, "Dependency unavailable", "The content store did not answer in time. Retry shortly."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "An unexpected error occurred."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("Request rejected: {Title}: {Detail}", title, detail);
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
