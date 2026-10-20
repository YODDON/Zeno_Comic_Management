using ComicManagement.Billing.Refunds;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace ComicManagement.Persistence;

internal sealed class RefundAwareForeignKeyIndexConvention(
    ProviderConventionSetBuilderDependencies dependencies)
    : ForeignKeyIndexConvention(dependencies)
{
    private static readonly string[][] RedundantIndexes =
    [
        [
            nameof(RefundRequest.PaymentOrderId),
            nameof(RefundRequest.UserId),
            nameof(RefundRequest.PaymentOrderType),
            nameof(RefundRequest.OriginalAmountVnd),
        ],
        [
            nameof(RefundRequest.PaymentOrderId),
            nameof(RefundRequest.UserId),
            nameof(RefundRequest.PaymentOrderType),
            nameof(RefundRequest.RequiredRecoveryCoins),
        ],
    ];

    public override void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        base.ProcessModelFinalizing(modelBuilder, context);

        var entityType = modelBuilder.Metadata.FindEntityType(typeof(RefundRequest));
        if (entityType is null)
        {
            return;
        }

        foreach (var propertyNames in RedundantIndexes)
        {
            var index = entityType.GetIndexes().SingleOrDefault(candidate =>
                candidate.Properties.Select(property => property.Name)
                    .SequenceEqual(propertyNames, StringComparer.Ordinal));
            if (index is not null)
            {
                ((IMutableEntityType)entityType).RemoveIndex(index);
            }
        }
    }

    public override void ProcessIndexRemoved(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionIndex index,
        IConventionContext<IConventionIndex> context)
    {
        if (index.DeclaringEntityType.ClrType == typeof(RefundRequest) &&
            RedundantIndexes.Any(propertyNames =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(propertyNames, StringComparer.Ordinal)))
        {
            return;
        }

        base.ProcessIndexRemoved(entityTypeBuilder, index, context);
    }
}
