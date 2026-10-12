using System.Data;
using ComicManagement.Catalog;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Publishing;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class CatalogPublicationStore(AppDbContext dbContext) : ICatalogPublicationStore
{
    public Task<CatalogPublicationResult> PublishComicAsync(
        Guid comicId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        ExecuteComicAsync(
            comicId,
            async comic =>
            {
                var hasValidPublishedChapter = await dbContext.Chapters
                    .Where(chapter =>
                        chapter.ComicId == comic.Id &&
                        chapter.PublicationStatus == ChapterPublicationStatuses.Published)
                    .AnyAsync(
                        chapter => dbContext.ChapterPages.Any(page =>
                            page.ChapterId == chapter.Id &&
                            page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                            page.Width > 0 &&
                            page.Height > 0),
                        cancellationToken);

                comic.Publish(hasValidPublishedChapter, expectedVersion, occurredAt);
            },
            cancellationToken);

    public Task<CatalogPublicationResult> HideComicAsync(
        Guid comicId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        ExecuteComicAsync(
            comicId,
            comic =>
            {
                comic.Hide(expectedVersion, occurredAt);
                return Task.CompletedTask;
            },
            cancellationToken);

    public async Task<CatalogPublicationResult> PublishChapterAsync(
        Guid chapterId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var chapterIdentity = await dbContext.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.Id == chapterId)
            .Select(chapter => new { chapter.Id, chapter.ComicId })
            .SingleOrDefaultAsync(cancellationToken);
        if (chapterIdentity is null)
        {
            return NotFound();
        }

        var comic = await LockComicAsync(chapterIdentity.ComicId, cancellationToken);
        if (comic is null)
        {
            return NotFound();
        }

        var chapter = await LockChapterAsync(chapterId, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        var hasValidPage = await dbContext.ChapterPages.AnyAsync(
            page =>
                page.ChapterId == chapter.Id &&
                page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                page.Width > 0 &&
                page.Height > 0,
            cancellationToken);

        return await SaveChapterAsync(
            chapter,
            () => chapter.Publish(hasValidPage, expectedVersion, occurredAt),
            transaction,
            cancellationToken);
    }

    public async Task<CatalogPublicationResult> HideChapterAsync(
        Guid chapterId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var chapterIdentity = await dbContext.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.Id == chapterId)
            .Select(chapter => new { chapter.Id, chapter.ComicId })
            .SingleOrDefaultAsync(cancellationToken);
        if (chapterIdentity is null)
        {
            return NotFound();
        }

        var comic = await LockComicAsync(chapterIdentity.ComicId, cancellationToken);
        if (comic is null)
        {
            return NotFound();
        }

        var chapter = await LockChapterAsync(chapterId, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        var canHide = true;
        if (comic.PublicationStatus == ComicPublicationStatuses.Published &&
            chapter.PublicationStatus == ChapterPublicationStatuses.Published)
        {
            var otherValidPublishedChapterExists = await dbContext.Chapters
                .Where(candidate =>
                    candidate.ComicId == comic.Id &&
                    candidate.Id != chapter.Id &&
                    candidate.PublicationStatus == ChapterPublicationStatuses.Published)
                .AnyAsync(
                    candidate => dbContext.ChapterPages.Any(page =>
                        page.ChapterId == candidate.Id &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0),
                    cancellationToken);
            canHide = otherValidPublishedChapterExists;
        }

        return await SaveChapterAsync(
            chapter,
            () => chapter.Hide(canHide, expectedVersion, occurredAt),
            transaction,
            cancellationToken);
    }

    private async Task<CatalogPublicationResult> ExecuteComicAsync(
        Guid comicId,
        Func<Comic, Task> transition,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var comic = await LockComicAsync(comicId, cancellationToken);
        if (comic is null)
        {
            return NotFound();
        }

        try
        {
            await transition(comic);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Succeeded(comic);
        }
        catch (CatalogRuleViolationException exception)
        {
            return RuleViolation(exception);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict();
        }
    }

    private async Task<CatalogPublicationResult> SaveChapterAsync(
        Chapter chapter,
        Action transition,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            transition();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Succeeded(chapter);
        }
        catch (CatalogRuleViolationException exception)
        {
            return RuleViolation(exception);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict();
        }
    }

    private async Task<Comic?> LockComicAsync(
        Guid comicId,
        CancellationToken cancellationToken)
    {
        var comics = await dbContext.Comics
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.comics WHERE id = {comicId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(cancellationToken);
        return comics.SingleOrDefault();
    }

    private async Task<Chapter?> LockChapterAsync(
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var chapters = await dbContext.Chapters
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.chapters WHERE id = {chapterId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(cancellationToken);
        return chapters.SingleOrDefault();
    }

    private static CatalogPublicationResult Succeeded(Comic comic) => new(
        CatalogPublicationOutcome.Succeeded,
        new CatalogPublicationState(
            comic.Id,
            comic.PublicationStatus,
            comic.Version,
            comic.FirstPublishedAt));

    private static CatalogPublicationResult Succeeded(Chapter chapter) => new(
        CatalogPublicationOutcome.Succeeded,
        new CatalogPublicationState(
            chapter.Id,
            chapter.PublicationStatus,
            chapter.Version,
            null));

    private static CatalogPublicationResult NotFound() => new(
        CatalogPublicationOutcome.NotFound);

    private static CatalogPublicationResult ConcurrencyConflict() => new(
        CatalogPublicationOutcome.ConcurrencyConflict);

    private static CatalogPublicationResult RuleViolation(
        CatalogRuleViolationException exception) => new(
            exception.Violation == CatalogRuleViolation.ConcurrencyConflict
                ? CatalogPublicationOutcome.ConcurrencyConflict
                : CatalogPublicationOutcome.PublicationPreconditionFailed);
}
