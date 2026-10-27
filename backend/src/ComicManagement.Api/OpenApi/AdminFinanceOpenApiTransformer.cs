using System.Text.Json.Nodes;
using ComicManagement.Administration.Finance;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class AdminFinanceOpenApiTransformer :
    IOpenApiOperationTransformer,
    IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Description.HttpMethod, "GET", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        context.Description.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        if (controller == "AdminFinance")
        {
            SetParameterEnum(operation, "status", AdminFinanceContractValues.PaymentOrderStatuses);
            SetParameterEnum(operation, "type", AdminFinanceContractValues.PaymentOrderTypes);
            SetParameterEnum(operation, "operationType", AdminFinanceContractValues.WalletOperationTypes);
        }
        else if (controller == "AdminRefunds")
        {
            SetParameterEnum(operation, "status", AdminFinanceContractValues.RefundStatuses);
        }

        return Task.CompletedTask;
    }

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(AdminPaymentListItem))
        {
            SetPropertyEnum(schema, "orderStatus", AdminFinanceContractValues.PaymentOrderStatuses);
            SetPropertyEnum(schema, "orderType", AdminFinanceContractValues.PaymentOrderTypes);
        }
        else if (context.JsonTypeInfo.Type == typeof(AdminRefundListItem))
        {
            SetPropertyEnum(schema, "status", AdminFinanceContractValues.RefundStatuses);
        }
        else if (context.JsonTypeInfo.Type == typeof(AdminLedgerItem))
        {
            SetPropertyEnum(schema, "operationType", AdminFinanceContractValues.WalletOperationTypes);
        }

        return Task.CompletedTask;
    }

    private static void SetParameterEnum(
        OpenApiOperation operation,
        string parameterName,
        IReadOnlyList<string> values)
    {
        var parameter = operation.Parameters?.SingleOrDefault(item => item.Name == parameterName);
        if (parameter?.Schema is OpenApiSchema schema)
        {
            schema.Enum = Values(values);
        }
    }

    private static void SetPropertyEnum(
        OpenApiSchema schema,
        string propertyName,
        IReadOnlyList<string> values)
    {
        if (schema.Properties?.TryGetValue(propertyName, out var property) == true &&
            property is OpenApiSchema propertySchema)
        {
            propertySchema.Enum = Values(values);
        }
    }

    private static List<JsonNode> Values(IReadOnlyList<string> values) =>
        values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
}
