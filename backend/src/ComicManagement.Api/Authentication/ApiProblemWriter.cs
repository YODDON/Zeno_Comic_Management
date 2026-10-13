using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Authentication;

public sealed class ApiErrorResponse : ProblemDetails
{
    public required string ErrorCode { get; init; }

    public required string Message { get; init; }

    public required string TraceId { get; init; }
}

internal sealed class ApiProblemWriter(IdentityAuthenticationSettings settings)
{
    internal ApiErrorResponse Create(
        HttpContext httpContext,
        int status,
        string errorCode,
        string title,
        string detail)
    {
        return new ApiErrorResponse
        {
            Type = new Uri(
                settings.ErrorDocumentationBaseUri,
                Uri.EscapeDataString(errorCode)).AbsoluteUri,
            Title = title,
            Status = status,
            Detail = detail,
            ErrorCode = errorCode,
            Message = detail,
            TraceId = httpContext.TraceIdentifier,
        };
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
