using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ComicManagement.Api.Deployment;

internal static class DeploymentHealthResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web);

    internal static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(
                new DeploymentHealthResponse(report.Status.ToString()),
                SerializerOptions),
            context.RequestAborted);
    }

    private sealed record DeploymentHealthResponse(string Status);
}
