using ComicManagement.Missions.Receipts;
using Xunit;

namespace ComicManagement.Missions.UnitTests;

public sealed class ActionReceiptTests
{
    [Fact]
    public void Receipt_derives_vietnam_date_from_authoritative_event_time()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 18, 18, 30, 0, TimeSpan.Zero);

        var receipt = ActionReceipt.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Identity.UserLoggedIn.v1",
            Guid.NewGuid(),
            null,
            new string('a', ActionReceipt.PayloadFingerprintLength),
            occurredAt,
            occurredAt.AddMinutes(5));

        Assert.Equal(new DateOnly(2026, 10, 19), receipt.OccurredLocalDate);
    }
}
