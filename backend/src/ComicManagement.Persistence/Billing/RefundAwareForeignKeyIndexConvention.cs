using ComicManagement.Billing.Refunds;
using ComicManagement.Wallet.Reservations;
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

    private static readonly string[][] RedundantReservationIndexes =
    [
        [nameof(CoinReservation.RefundRequestId), nameof(CoinReservation.UserId)],
        [nameof(CoinReservation.WalletOperationId), nameof(CoinReservation.UserId)],
        [nameof(CoinReservation.PaidCoinLotId), nameof(CoinReservation.UserId)],
    ];

    private static readonly string[][] RedundantTransferIndexes =
    [
        [nameof(RefundTransferRecord.RefundRequestId), nameof(RefundTransferRecord.UserId)],
        [nameof(RefundTransferRecord.ResolutionOperationId), nameof(RefundTransferRecord.UserId)],
    ];

    public override void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        base.ProcessModelFinalizing(modelBuilder, context);

        RemoveIndexes(modelBuilder.Metadata.FindEntityType(typeof(RefundRequest)),
            RedundantIndexes);
        RemoveIndexes(modelBuilder.Metadata.FindEntityType(typeof(CoinReservation)),
            RedundantReservationIndexes);
        RemoveIndexes(modelBuilder.Metadata.FindEntityType(typeof(RefundTransferRecord)),
            RedundantTransferIndexes);
    }

    public override void ProcessIndexRemoved(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionIndex index,
        IConventionContext<IConventionIndex> context)
    {
        if (IsRedundant(index))
        {
            return;
        }

        base.ProcessIndexRemoved(entityTypeBuilder, index, context);
    }

    private static void RemoveIndexes(
        IReadOnlyEntityType? entityType,
        IEnumerable<string[]> redundantIndexes)
    {
        if (entityType is null)
        {
            return;
        }

        foreach (var propertyNames in redundantIndexes)
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

    private static bool IsRedundant(IConventionIndex index)
    {
        var candidates = index.DeclaringEntityType.ClrType == typeof(RefundRequest)
            ? RedundantIndexes
            : index.DeclaringEntityType.ClrType == typeof(CoinReservation)
                ? RedundantReservationIndexes
                : index.DeclaringEntityType.ClrType == typeof(RefundTransferRecord)
                    ? RedundantTransferIndexes
                : [];
        return candidates.Any(propertyNames =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(propertyNames, StringComparer.Ordinal));
    }
}
