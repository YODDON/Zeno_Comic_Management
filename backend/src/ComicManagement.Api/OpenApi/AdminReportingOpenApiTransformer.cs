using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class AdminReportingOpenApiTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        context.Description.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        if (!string.Equals(controller, "AdminReporting", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        operation.Description =
            "Admin-only, no-store reporting read. Date values are Asia/Ho_Chi_Minh " +
            "calendar dates and form the half-open UTC interval [from 00:00, to+1 00:00). " +
            "The inclusive range defaults to 30 days and cannot exceed 366 days.";
        foreach (var parameter in operation.Parameters ?? [])
        {
            switch (parameter.Name)
            {
                case "from":
                case "to":
                    parameter.Description = "Vietnam-local calendar date (YYYY-MM-DD).";
                    break;
                case "page":
                    parameter.Description =
                        "One-based page number. The computed offset " +
                        "(page - 1) * pageSize must not exceed 2147483647; " +
                        "larger offsets return 400 VALIDATION_FAILED.";
                    break;
                case "pageSize":
                    parameter.Description =
                        "Items per page; default 30, maximum 100. Together with page, " +
                        "the computed offset must fit a signed 32-bit integer.";
                    break;
                case "action":
                case "targetType":
                    parameter.Description = "Exact-match filter, maximum 120 characters.";
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
