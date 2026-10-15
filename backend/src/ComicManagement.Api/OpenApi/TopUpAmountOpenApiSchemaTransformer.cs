using System.Reflection;
using ComicManagement.Api.Controllers;
using ComicManagement.Billing.Payments;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class TopUpAmountOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is PropertyInfo property &&
            property.DeclaringType == typeof(CreateTopUpRequest) &&
            property.Name == nameof(CreateTopUpRequest.AmountVnd))
        {
            schema.MultipleOf = TopUpAmountPolicy.AmountStepVnd;
        }

        return Task.CompletedTask;
    }
}
