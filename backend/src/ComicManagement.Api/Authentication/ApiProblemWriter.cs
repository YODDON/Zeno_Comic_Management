using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Authentication;

internal sealed class ApiProblemWriter(IdentityAuthenticationSettings settings)
{
    internal ProblemDetails Create(
        HttpContext httpContext,
        int status,
        string errorCode,
        string title,
        string detail)
    {
        var problem = new ProblemDetails
        {
            Type = new Uri(
                settings.ErrorDocumentationBaseUri,
                Uri.EscapeDataString(errorCode)).AbsoluteUri,
            Title = title,
            Status = status,
            Detail = detail,
        };
        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        return problem;
    }

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
            Create(httpContext, status, errorCode, title, detail),
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            contentType: "application/problem+json",
            cancellationToken: httpContext.RequestAborted);
    }
}
