using ComicManagement.Api.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class RefundOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(CreateRefundRequest))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("paymentOrderId");
            schema.Required.Add("reason");
            schema.Required.Add("recipient");
        }
        else if (context.JsonTypeInfo.Type == typeof(RefundRecipientRequest))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("bankCode");
            schema.Required.Add("accountNumber");
            schema.Required.Add("accountHolder");
        }

        return Task.CompletedTask;
    }
}
