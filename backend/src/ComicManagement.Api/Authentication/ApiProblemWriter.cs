using System.Text.Json;

namespace ComicManagement.Api.Authentication;

internal sealed class ApiProblemWriter(IdentityAuthenticationSettings settings)
{
    internal Task WriteAsync(
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
                type = new Uri(
                    settings.ErrorDocumentationBaseUri,
                    Uri.EscapeDataString(errorCode)).AbsoluteUri,
                title,
                status,
                errorCode,
                traceId = httpContext.TraceIdentifier,
                detail,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            contentType: "application/problem+json",
            cancellationToken: httpContext.RequestAborted);
    }
}
