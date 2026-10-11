using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Shared.UnitTests;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_returns_current_utc_instant()
    {
        var before = DateTimeOffset.UtcNow;

        var actual = new SystemClock().UtcNow;

        var after = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.InRange(actual, before, after);
    }
}
