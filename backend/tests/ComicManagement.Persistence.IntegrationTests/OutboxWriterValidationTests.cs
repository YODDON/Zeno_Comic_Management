using ComicManagement.Persistence;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class OutboxWriterValidationTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enqueue_rejects_missing_event_type(string eventType)
    {
        using var context = CreateContext();
        var writer = new EfOutboxWriter(context, new StubClock(UtcNow));

        Assert.Throws<ArgumentException>(() =>
            writer.Enqueue(eventType, new { value = 1 }, UtcNow));
    }

    [Fact]
    public void Enqueue_rejects_event_type_over_200_characters()
    {
        using var context = CreateContext();
        var writer = new EfOutboxWriter(context, new StubClock(UtcNow));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.Enqueue(new string('a', 201), new { value = 1 }, UtcNow));
    }

    [Fact]
    public void Enqueue_rejects_null_payload()
    {
        using var context = CreateContext();
        var writer = new EfOutboxWriter(context, new StubClock(UtcNow));

        Assert.Throws<ArgumentNullException>(() =>
            writer.Enqueue<object>("tests.validation", null!, UtcNow));
    }

    [Fact]
    public void Enqueue_rejects_non_utc_occurred_at()
    {
        using var context = CreateContext();
        var writer = new EfOutboxWriter(context, new StubClock(UtcNow));
        var vietnamTime = UtcNow.ToOffset(TimeSpan.FromHours(7));

        Assert.Throws<ArgumentException>(() =>
            writer.Enqueue("tests.validation", new { value = 1 }, vietnamTime));
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;
        return new AppDbContext(options);
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
