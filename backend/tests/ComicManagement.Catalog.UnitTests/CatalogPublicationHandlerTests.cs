using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class CatalogPublicationHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId = Guid.NewGuid();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Handler_rejects_invalid_expected_version_without_calling_store(
        long expectedVersion)
    {
        var store = new RecordingStore();
        var handler = new CatalogPublicationHandler(store, new StubClock(Now));

        var result = await handler.PublishComicAsync(
            Guid.NewGuid(),
            expectedVersion,
            ActorId,
            "trace-1");

        Assert.Equal(CatalogPublicationOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task Handler_owns_transition_and_passes_actor_clock_and_correlation_to_store()
    {
        var comic = CreateComic();
        var store = new RecordingStore(comic: comic);
        var handler = new CatalogPublicationHandler(store, new StubClock(Now));

        var result = await handler.PublishComicAsync(
            comic.Id,
            1,
            ActorId,
            "trace-publish");

        Assert.Equal(CatalogPublicationOutcome.Succeeded, result.Outcome);
        Assert.Equal(ComicPublicationStatuses.Published, comic.PublicationStatus);
        Assert.Equal(Now, comic.FirstPublishedAt);
        Assert.Equal(ActorId, store.Audit!.ActorId);
        Assert.Equal("Catalog.ComicPublished", store.Audit.Action);
        Assert.Equal("Comic", store.Audit.TargetType);
        Assert.Equal("trace-publish", store.Audit.CorrelationId);
        Assert.Equal(Now, store.OccurredAt);
    }

    [Fact]
    public async Task Handler_applies_last_valid_chapter_rule_from_store_facts()
    {
        var chapter = Chapter.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Chapter 1",
            1,
            0,
            false,
            Now);
        chapter.Publish(true, 1, Now.AddMinutes(1));
        var store = new RecordingStore(chapter: chapter)
        {
            ChapterFacts = new ChapterPublicationFacts(
                ComicPublicationStatuses.Published,
                true,
                false),
        };
        var handler = new CatalogPublicationHandler(store, new StubClock(Now.AddMinutes(2)));

        var exception = await Assert.ThrowsAsync<CatalogRuleViolationException>(() =>
            handler.HideChapterAsync(
                chapter.Id,
                2,
                ActorId,
                "trace-hide"));

        Assert.Equal(CatalogRuleViolation.PublicationPreconditionFailed, exception.Violation);
    }

    private static Comic CreateComic() => Comic.Create(
        Guid.NewGuid(),
        "handler-comic",
        "Handler Comic",
        string.Empty,
        ComicSeriesStatuses.Ongoing,
        null,
        0,
        false,
        Now);

    private sealed class RecordingStore(
        Comic? comic = null,
        Chapter? chapter = null) : ICatalogPublicationStore
    {
        public int CallCount { get; private set; }

        public CatalogCommandAudit? Audit { get; private set; }

        public DateTimeOffset OccurredAt { get; private set; }

        public ComicPublicationFacts ComicFacts { get; set; } = new(true);

        public ChapterPublicationFacts ChapterFacts { get; set; } = new(
            ComicPublicationStatuses.Draft,
            true,
            true);

        public Task<CatalogPublicationResult> ExecuteComicAsync(
            Guid comicId,
            CatalogCommandAudit audit,
            Action<Comic, ComicPublicationFacts> transition,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Audit = audit;
            OccurredAt = occurredAt;
            var aggregate = comic ?? throw new InvalidOperationException("Comic was not supplied.");
            transition(aggregate, ComicFacts);
            return Task.FromResult(new CatalogPublicationResult(
                CatalogPublicationOutcome.Succeeded,
                new CatalogPublicationState(
                    comicId,
                    aggregate.PublicationStatus,
                    aggregate.Version,
                    aggregate.FirstPublishedAt,
                    aggregate.Slug)));
        }

        public Task<CatalogPublicationResult> ExecuteChapterAsync(
            Guid chapterId,
            CatalogCommandAudit audit,
            Action<Chapter, ChapterPublicationFacts> transition,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Audit = audit;
            OccurredAt = occurredAt;
            var aggregate = chapter ?? throw new InvalidOperationException("Chapter was not supplied.");
            transition(aggregate, ChapterFacts);
            return Task.FromResult(new CatalogPublicationResult(
                CatalogPublicationOutcome.Succeeded,
                new CatalogPublicationState(
                    chapterId,
                    aggregate.PublicationStatus,
                    aggregate.Version,
                    null)));
        }
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
