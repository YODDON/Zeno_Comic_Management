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
            now);

        Assert.Equal(CoinReservationStatuses.Reserved, reservation.Status);
        Assert.Equal(10_000, reservation.AmountCoins);
        Assert.Equal(now, reservation.CreatedAt);
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
            DateTimeOffset.UtcNow));
    }
}
