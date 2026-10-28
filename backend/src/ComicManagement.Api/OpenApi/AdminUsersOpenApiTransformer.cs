using System.Text.Json.Nodes;
using ComicManagement.Identity.Administration;
using ComicManagement.Missions.Administration;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ComicManagement.Api.OpenApi;

public sealed class AdminUsersOpenApiTransformer :
    IOpenApiOperationTransformer,
    IOpenApiSchemaTransformer
{
    private static readonly List<JsonNode> Statuses = Values("Active", "Disabled");
    private static readonly List<JsonNode> Roles = Values("User", "Admin");
    private static readonly List<JsonNode> RewardOutcomes = Values("Granted", "DeniedDailyCap");
    private static readonly List<JsonNode> PaymentStatuses = Values("Pending", "Expired", "Succeeded");
    private static readonly List<JsonNode> PaymentTypes = Values("TopUp", "VipPurchase");

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        context.Description.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        if (controller == "AdminUsers" && context.Description.HttpMethod == "GET")
        {
            var status = operation.Parameters?.SingleOrDefault(parameter => parameter.Name == "status");
            if (status?.Schema is OpenApiSchema schema)
            {
                schema.Enum = Statuses;
            }
        }
        else if (controller == "AdminMissionRewards" && context.Description.HttpMethod == "GET")
        {
            var outcome = operation.Parameters?.SingleOrDefault(parameter => parameter.Name == "outcome");
            if (outcome?.Schema is OpenApiSchema schema) schema.Enum = RewardOutcomes;
        }
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(AdminUserListItem) && schema.Properties is not null)
        {
            if (schema.Properties.TryGetValue("status", out var status) && status is OpenApiSchema statusSchema) statusSchema.Enum = Statuses;
            if (schema.Properties.TryGetValue("role", out var role) && role is OpenApiSchema roleSchema) roleSchema.Enum = Roles;
        }
        else if (context.JsonTypeInfo.Type == typeof(AdminMissionRewardItem) && schema.Properties is not null &&
                 schema.Properties.TryGetValue("outcome", out var outcome) && outcome is OpenApiSchema outcomeSchema)
        {
            outcomeSchema.Enum = RewardOutcomes;
        }
        else if (context.JsonTypeInfo.Type == typeof(AdminUserPaymentSummary) && schema.Properties is not null)
        {
            if (schema.Properties.TryGetValue("status", out var status) && status is OpenApiSchema statusSchema)
            {
                statusSchema.Enum = PaymentStatuses;
            }
            if (schema.Properties.TryGetValue("orderType", out var orderType) && orderType is OpenApiSchema typeSchema)
            {
                typeSchema.Enum = PaymentTypes;
            }
        }
        return Task.CompletedTask;
    }

    private static List<JsonNode> Values(params string[] values) =>
        values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
}
