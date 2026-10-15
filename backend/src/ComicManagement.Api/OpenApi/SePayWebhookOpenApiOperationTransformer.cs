using ComicManagement.Api.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class SePayWebhookOpenApiOperationTransformer :
    IOpenApiOperationTransformer
{
    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Description.ActionDescriptor.RouteValues.TryGetValue(
                "controller",
                out var controller) ||
            controller != "SePayWebhook")
        {
            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(
            typeof(SePayWebhookPayloadContract),
            null,
            cancellationToken);
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter.Name == "X-SePay-Signature")
            {
                if (parameter is OpenApiParameter signatureParameter)
                {
                    signatureParameter.Required = true;
                }

                if (parameter.Schema is OpenApiSchema signatureSchema)
                {
                    signatureSchema.Pattern = "^sha256=[0-9A-Fa-f]{64}$";
                }
            }
            else if (parameter.Name == "X-SePay-Timestamp")
            {
                if (parameter is OpenApiParameter timestampParameter)
                {
                    timestampParameter.Required = true;
                }

                if (parameter.Schema is OpenApiSchema timestampSchema)
                {
                    timestampSchema.Pattern = "^[0-9]+$";
                }
            }
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description =
                "Raw JSON payload signed over X-SePay-Timestamp + '.' + the exact request body.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = schema,
                },
            },
        };
    }
}
