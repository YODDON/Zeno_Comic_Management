using ComicManagement.Catalog.Publishing;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class CatalogPublicationHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Handler_rejects_invalid_expected_version_without_calling_store(
        long expectedVersion)
    {
        var store = new RecordingStore();
        var handler = new CatalogPublicationHandler(store, new StubClock(Now));

        var result = await handler.PublishComicAsync(Guid.NewGuid(), expectedVersion);

        Assert.Equal(CatalogPublicationOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task Handler_passes_authoritative_clock_to_store()
    {
        var store = new RecordingStore();
        var handler = new CatalogPublicationHandler(store, new StubClock(Now));
        var comicId = Guid.NewGuid();

        var result = await handler.PublishComicAsync(comicId, 1);

        Assert.Equal(CatalogPublicationOutcome.Succeeded, result.Outcome);
        Assert.Equal(comicId, store.AggregateId);
        Assert.Equal(1, store.ExpectedVersion);
        Assert.Equal(Now, store.OccurredAt);
    }

    private sealed class RecordingStore : ICatalogPublicationStore
    {
        public int CallCount { get; private set; }

        public Guid AggregateId { get; private set; }

        public long ExpectedVersion { get; private set; }

        public DateTimeOffset OccurredAt { get; private set; }

        public Task<CatalogPublicationResult> PublishComicAsync(
            Guid comicId,
            long expectedVersion,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            AggregateId = comicId;
            ExpectedVersion = expectedVersion;
            OccurredAt = occurredAt;
            return Task.FromResult(new CatalogPublicationResult(
                CatalogPublicationOutcome.Succeeded,
                new CatalogPublicationState(comicId, "Published", 2, occurredAt)));
        }

        public Task<CatalogPublicationResult> HideComicAsync(
            Guid comicId,
            long expectedVersion,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CatalogPublicationResult> PublishChapterAsync(
            Guid chapterId,
            long expectedVersion,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CatalogPublicationResult> HideChapterAsync(
            Guid chapterId,
            long expectedVersion,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
