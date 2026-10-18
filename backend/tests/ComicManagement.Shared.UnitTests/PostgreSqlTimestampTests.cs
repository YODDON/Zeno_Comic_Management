using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Shared.UnitTests;

public sealed class PostgreSqlTimestampTests
{
    [Fact]
    public void Normalize_truncates_sub_microsecond_ticks_and_preserves_utc()
    {
        var value = new DateTimeOffset(638964684000000007, TimeSpan.Zero);

        var normalized = PostgreSqlTimestamp.Normalize(value);

        Assert.Equal(638964684000000000, normalized.Ticks);
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
    }

    [Fact]
    public void Normalize_rejects_non_utc_timestamp()
    {
        var value = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(7));

        Assert.Throws<ArgumentException>(() => PostgreSqlTimestamp.Normalize(value));
    }
}
