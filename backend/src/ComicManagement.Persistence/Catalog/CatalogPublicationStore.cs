using System.Data;
using System.Text.Json;
using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Publishing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ComicManagement.Persistence;

internal sealed class CatalogPublicationStore(AppDbContext dbContext) : ICatalogPublicationStore
{
    public async Task<CatalogPublicationResult> ExecuteComicAsync(
        Guid comicId,
        CatalogCommandAudit audit,
        Action<Comic, ComicPublicationFacts> transition,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var comic = await LockComicAsync(comicId, cancellationToken);
        if (comic is null)
        {
            return NotFound();
        }

        var facts = new ComicPublicationFacts(await HasValidPublishedChapterAsync(
            comic.Id,
            cancellationToken));
        var before = new { comic.PublicationStatus, comic.Slug, comic.Version };

        try
        {
            transition(comic, facts);
            AddAudit(
                audit,
                comic.Id,
                occurredAt,
                JsonSerializer.Serialize(new
                {
                    Before = before,
                    After = new { comic.PublicationStatus, comic.Slug, comic.Version },
                }));
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
        catch (DbUpdateException exception) when (IsSlugConflict(exception))
        {
            return new CatalogPublicationResult(CatalogPublicationOutcome.SlugConflict);
        }
    }

    public async Task<CatalogPublicationResult> ExecuteChapterAsync(
        Guid chapterId,
        CatalogCommandAudit audit,
        Action<Chapter, ChapterPublicationFacts> transition,
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

        var facts = new ChapterPublicationFacts(
            comic.PublicationStatus,
            await HasValidPageAsync(chapter.Id, cancellationToken),
            await HasOtherValidPublishedChapterAsync(
                comic.Id,
                chapter.Id,
                cancellationToken));
        var before = new { chapter.PublicationStatus, chapter.Version };

        try
        {
            transition(chapter, facts);
            AddAudit(
                audit,
                chapter.Id,
                occurredAt,
                JsonSerializer.Serialize(new
                {
                    Before = before,
                    After = new { chapter.PublicationStatus, chapter.Version },
                }));
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

    private Task<bool> HasValidPublishedChapterAsync(
        Guid comicId,
        CancellationToken cancellationToken) => dbContext.Chapters
        .Where(chapter =>
            chapter.ComicId == comicId &&
            chapter.PublicationStatus == ChapterPublicationStatuses.Published)
        .AnyAsync(
            chapter => dbContext.ChapterPages.Any(page =>
                page.ChapterId == chapter.Id &&
                page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                page.Width > 0 &&
                page.Height > 0),
            cancellationToken);

    private Task<bool> HasValidPageAsync(
        Guid chapterId,
        CancellationToken cancellationToken) => dbContext.ChapterPages.AnyAsync(
        page =>
            page.ChapterId == chapterId &&
            page.AssetStatus == ChapterPageAssetStatuses.Ready &&
            page.Width > 0 &&
            page.Height > 0,
        cancellationToken);

    private Task<bool> HasOtherValidPublishedChapterAsync(
        Guid comicId,
        Guid excludedChapterId,
        CancellationToken cancellationToken) => dbContext.Chapters
        .Where(candidate =>
            candidate.ComicId == comicId &&
            candidate.Id != excludedChapterId &&
            candidate.PublicationStatus == ChapterPublicationStatuses.Published)
        .AnyAsync(
            candidate => dbContext.ChapterPages.Any(page =>
                page.ChapterId == candidate.Id &&
                page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                page.Width > 0 &&
                page.Height > 0),
            cancellationToken);

    private void AddAudit(
        CatalogCommandAudit audit,
        Guid targetId,
        DateTimeOffset occurredAt,
        string redactedChanges) => dbContext.AuditLogs.Add(AuditLog.Create(
        Guid.NewGuid(),
        audit.ActorId,
        audit.Action,
        audit.TargetType,
        targetId,
        audit.CorrelationId,
        occurredAt,
        redactedChanges));

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

    private static bool IsSlugConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_comics_slug",
        };

    private static CatalogPublicationResult Succeeded(Comic comic) => new(
        CatalogPublicationOutcome.Succeeded,
        new CatalogPublicationState(
            comic.Id,
            comic.PublicationStatus,
            comic.Version,
            comic.FirstPublishedAt,
            comic.Slug));

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
