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
        else if (context.JsonTypeInfo.Type == typeof(RecordRefundTransferRequest))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("expectedVersion");
            schema.Required.Add("bankReference");
            schema.Required.Add("evidenceReference");
            schema.Required.Add("reportedOutcome");
        }
        else if (context.JsonTypeInfo.Type == typeof(ResolveRefundTransferRequest))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("expectedVersion");
            schema.Required.Add("transferRecordId");
            schema.Required.Add("confirmationNote");
        }

        return Task.CompletedTask;
    }
}
