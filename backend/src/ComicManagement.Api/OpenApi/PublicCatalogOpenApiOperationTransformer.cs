using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class PublicCatalogOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.RouteValues.TryGetValue(
                "controller",
                out var controller) &&
            controller == "PublicCatalog")
        {
            var parameter = operation.Parameters?.SingleOrDefault(
                value => value.Name == "seriesStatus");
            if (parameter?.Schema is OpenApiSchema schema)
            {
                schema.Enum =
                [
                    JsonValue.Create("Ongoing")!,
                    JsonValue.Create("Completed")!,
                    JsonValue.Create("Hiatus")!,
                ];
            }
        }

        return Task.CompletedTask;
    }
}
