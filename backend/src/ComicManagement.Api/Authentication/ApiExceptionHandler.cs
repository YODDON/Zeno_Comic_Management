using Microsoft.AspNetCore.Diagnostics;

namespace ComicManagement.Api.Authentication;

internal sealed class ApiExceptionHandler(
    ApiProblemWriter problemWriter,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            "Unhandled API exception {ExceptionType} for trace {TraceId}.",
            exception.GetType().FullName,
            httpContext.TraceIdentifier);

        await problemWriter.WriteAsync(
            httpContext,
            StatusCodes.Status500InternalServerError,
            "INTERNAL_ERROR",
            "Unexpected error",
            "The request could not be completed.");

        return true;
    }
}
