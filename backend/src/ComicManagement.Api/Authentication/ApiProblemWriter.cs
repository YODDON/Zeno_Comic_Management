namespace ComicManagement.Api.Authentication;

internal static class ApiProblemWriter
{
    internal static Task WriteAsync(
        HttpContext httpContext,
        int status,
        string errorCode,
        string title,
        string detail)
    {
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";

        return httpContext.Response.WriteAsJsonAsync(
            new
            {
                type = $"/errors/{errorCode}",
                title,
                status,
                errorCode,
                traceId = httpContext.TraceIdentifier,
                detail,
            },
            cancellationToken: httpContext.RequestAborted);
    }
}
