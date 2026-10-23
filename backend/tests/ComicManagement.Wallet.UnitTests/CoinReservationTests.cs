using ComicManagement.Wallet.Reservations;
using Xunit;

namespace ComicManagement.Wallet.UnitTests;

public sealed class CoinReservationTests
{
    [Fact]
    public void Factory_binds_refund_lot_operation_owner_and_full_amount()
    {
        var now = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var reservation = CoinReservation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "approval-key",
            new string('a', 64),
            10_000,
            3,
            Guid.NewGuid(),
            now.AddMinutes(-1),
            "Verified original lot",
            now);

        Assert.Equal(CoinReservationStatuses.Reserved, reservation.Status);
        Assert.Equal(10_000, reservation.AmountCoins);
        Assert.Equal(now, reservation.CreatedAt);
        Assert.Equal(RefundApprovalResultStatuses.RecoveryReserved,
            reservation.ApprovalResultStatus);
        Assert.Equal(3, reservation.ApprovalResultVersion);
        Assert.Null(reservation.ResolvedAt);
    }

    [Fact]
    public void Factory_rejects_non_positive_amount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoinReservation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "approval-key",
            new string('a', 64),
            0,
            3,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "Verified original lot",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Reservation_can_be_resolved_exactly_once()
    {
        var now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var reservation = CoinReservation.Create(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "approval-key",
            new string('a', 64), 10_000, 3, Guid.NewGuid(), now.AddMinutes(-1),
            "Verified original lot", now);

        reservation.Consume(now.AddMinutes(1));

        Assert.Equal(CoinReservationStatuses.Consumed, reservation.Status);
        Assert.Equal(now.AddMinutes(1), reservation.ResolvedAt);
        Assert.Throws<InvalidOperationException>(() => reservation.Release(now.AddMinutes(2)));
    }
}
