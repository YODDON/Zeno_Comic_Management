using System.Data;
using System.Text.Json;
using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Interaction.Comments;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class CommentStore(
    AppDbContext dbContext,
    IConsistentReadExecutor consistentReadExecutor) : ICommentStore
{
    public Task<CommentPageResult> ListTargetAsync(string targetType, Guid targetId, int page, int pageSize, CancellationToken cancellationToken) =>
        consistentReadExecutor.ExecuteAsync<CommentPageResult>(async token =>
        {
            if (!await TargetAvailableAsync(targetType, targetId, token)) return new(CommentOutcome.TargetUnavailable);
            var query = dbContext.Comments.AsNoTracking().Where(comment =>
                comment.Status == CommentStatuses.Visible && comment.ParentId == null &&
                (targetType == CommentTargetTypes.Comic ? comment.ComicId == targetId : comment.ChapterId == targetId));
            return new(CommentOutcome.Succeeded, await PageAsync(query, page, pageSize, false, token));
        }, cancellationToken);

    public Task<CommentPageResult> ListRepliesAsync(Guid parentId, int page, int pageSize, CancellationToken cancellationToken) =>
        consistentReadExecutor.ExecuteAsync<CommentPageResult>(async token =>
        {
            var parent = await dbContext.Comments.AsNoTracking().SingleOrDefaultAsync(comment => comment.Id == parentId, token);
            if (parent is null || parent.ParentId is not null || parent.Status != CommentStatuses.Visible)
                return new(CommentOutcome.NotFound);
            var targetType = parent.ComicId.HasValue ? CommentTargetTypes.Comic : CommentTargetTypes.Chapter;
            var targetId = parent.ComicId ?? parent.ChapterId!.Value;
            if (!await TargetAvailableAsync(targetType, targetId, token)) return new(CommentOutcome.TargetUnavailable);
            var query = dbContext.Comments.AsNoTracking().Where(comment => comment.ParentId == parentId && comment.Status == CommentStatuses.Visible);
            return new(CommentOutcome.Succeeded, await PageAsync(query, page, pageSize, true, token));
        }, cancellationToken);

    public async Task<CommentPageResult> ListForModerationAsync(string? status, int page, int pageSize, CancellationToken cancellationToken)
        => await consistentReadExecutor.ExecuteAsync<CommentPageResult>(async token =>
        {
            var query = dbContext.Comments.AsNoTracking().AsQueryable();
            if (status is not null) query = query.Where(comment => comment.Status == status);
            return new(CommentOutcome.Succeeded, await PageAsync(query, page, pageSize, false, token));
        }, cancellationToken);

    public async Task<CommentResult> CreateAsync(Comment comment, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var targetType = comment.ComicId.HasValue ? CommentTargetTypes.Comic : CommentTargetTypes.Chapter;
        var targetId = comment.ComicId ?? comment.ChapterId!.Value;
        if (!await LockAndValidateTargetAsync(targetType, targetId, cancellationToken))
            return new(CommentOutcome.TargetUnavailable);
        if (comment.ParentId is { } parentId)
        {
            var parent = await LockAsync(parentId, forUpdate: false, cancellationToken);
            if (parent is null || parent.Status != CommentStatuses.Visible || parent.ParentId is not null ||
                parent.ComicId != comment.ComicId || parent.ChapterId != comment.ChapterId)
                return new(CommentOutcome.InvalidParent);
        }
        dbContext.Comments.Add(comment);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = await ItemAsync(comment.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(CommentOutcome.Succeeded, response);
    }

    public Task<CommentResult> EditAsync(Guid commentId, Guid userId, string body, long expectedVersion, DateTimeOffset now, CancellationToken cancellationToken) =>
        MutateOwnerAsync(commentId, userId, comment => comment.Edit(body, expectedVersion, now), cancellationToken);

    public Task<CommentResult> DeleteByOwnerAsync(Guid commentId, Guid userId, long expectedVersion, DateTimeOffset now, CancellationToken cancellationToken) =>
        MutateOwnerAsync(commentId, userId, comment => comment.Delete(expectedVersion, now), cancellationToken);

    public async Task<CommentResult> ModerateAsync(Guid commentId, string action, long expectedVersion, CommentModerationAudit audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var comment = await LockAsync(commentId, forUpdate: true, cancellationToken);
        if (comment is null) return new(CommentOutcome.NotFound);
        var before = new { comment.Status, comment.Version };
        try
        {
            if (action == CommentModerationActions.Hide) comment.Hide(expectedVersion, now);
            else if (action == CommentModerationActions.Delete) comment.Delete(expectedVersion, now);
            else comment.Restore(expectedVersion, now);
            var auditAction = action switch
            {
                CommentModerationActions.Hide => "Interaction.CommentHidden",
                CommentModerationActions.Delete => "Interaction.CommentDeleted",
                CommentModerationActions.Restore => "Interaction.CommentRestored",
                _ => throw new InvalidOperationException("Unsupported comment moderation action."),
            };
            dbContext.AuditLogs.Add(AuditLog.Create(
                Guid.NewGuid(), audit.ActorId, auditAction, "Comment", comment.Id,
                audit.CorrelationId, now, JsonSerializer.Serialize(new { Before = before, After = new { comment.Status, comment.Version }, Reason = audit.Reason })));
            await dbContext.SaveChangesAsync(cancellationToken);
            var response = await ItemAsync(comment.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(CommentOutcome.Succeeded, response);
        }
        catch (CommentRuleViolationException exception)
        {
            return RuleFailure(exception);
        }
        catch (DbUpdateConcurrencyException) { return new(CommentOutcome.ConcurrencyConflict); }
    }

    private async Task<CommentResult> MutateOwnerAsync(Guid id, Guid userId, Action<Comment> transition, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var comment = await LockAsync(id, forUpdate: true, cancellationToken);
        if (comment is null) return new(CommentOutcome.NotFound);
        if (comment.UserId != userId) return new(CommentOutcome.Forbidden);
        try
        {
            transition(comment);
            await dbContext.SaveChangesAsync(cancellationToken);
            var response = await ItemAsync(comment.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(CommentOutcome.Succeeded, response);
        }
        catch (CommentRuleViolationException exception) { return RuleFailure(exception); }
        catch (DbUpdateConcurrencyException) { return new(CommentOutcome.ConcurrencyConflict); }
    }

    private async Task<Comment?> LockAsync(Guid id, bool forUpdate, CancellationToken token)
    {
        var comments = forUpdate
            ? await dbContext.Comments.FromSqlInterpolated($"SELECT * FROM interaction.comments WHERE id = {id} FOR UPDATE").AsTracking().ToListAsync(token)
            : await dbContext.Comments.FromSqlInterpolated($"SELECT * FROM interaction.comments WHERE id = {id} FOR SHARE").AsTracking().ToListAsync(token);
        return comments.SingleOrDefault();
    }

    private Task<bool> TargetAvailableAsync(string targetType, Guid targetId, CancellationToken token)
    {
        if (targetType == CommentTargetTypes.Comic)
            return PublicComicVisibilityQuery.Apply(dbContext).AnyAsync(comic => comic.Id == targetId, token);
        return dbContext.Chapters.AnyAsync(chapter =>
            chapter.Id == targetId && chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
            PublicComicVisibilityQuery.Apply(dbContext).Any(comic => comic.Id == chapter.ComicId) &&
            dbContext.ChapterPages.Any(page => page.ChapterId == chapter.Id && page.AssetStatus == ChapterPageAssetStatuses.Ready && page.Width > 0 && page.Height > 0), token);
    }

    // Catalog publication commands lock Comic before Chapter. Comment creation
    // follows the same order, then locks the Ready-page rows and finally the
    // optional parent comment. Eligibility is evaluated only after those locks
    // are held so a waiting request cannot commit against a stale public state.
    private async Task<bool> LockAndValidateTargetAsync(string targetType, Guid targetId, CancellationToken token)
    {
        Guid comicId;
        Guid? chapterId = null;

        if (targetType == CommentTargetTypes.Chapter)
        {
            var identity = await dbContext.Chapters.AsNoTracking()
                .Where(chapter => chapter.Id == targetId)
                .Select(chapter => new { chapter.Id, chapter.ComicId })
                .SingleOrDefaultAsync(token);
            if (identity is null) return false;
            comicId = identity.ComicId;
            chapterId = identity.Id;
        }
        else
        {
            comicId = targetId;
        }

        var comic = (await dbContext.Comics
                .FromSqlInterpolated($"SELECT * FROM catalog.comics WHERE id = {comicId} FOR SHARE")
                .AsTracking()
                .ToListAsync(token))
            .SingleOrDefault();
        if (comic is null ||
            comic.PublicationStatus != ComicPublicationStatuses.Published ||
            comic.FirstPublishedAt is null)
            return false;

        var chapters = chapterId.HasValue
            ? await dbContext.Chapters
                .FromSqlInterpolated($"SELECT * FROM catalog.chapters WHERE id = {chapterId.Value} FOR SHARE")
                .AsTracking()
                .ToListAsync(token)
            : await dbContext.Chapters
                .FromSqlInterpolated($"SELECT * FROM catalog.chapters WHERE comic_id = {comicId} ORDER BY id FOR SHARE")
                .AsTracking()
                .ToListAsync(token);

        if (chapterId.HasValue &&
            (chapters.Count != 1 ||
             chapters[0].ComicId != comicId ||
             chapters[0].PublicationStatus != ChapterPublicationStatuses.Published))
            return false;

        var pages = chapterId.HasValue
            ? await dbContext.ChapterPages
                .FromSqlInterpolated($"SELECT * FROM catalog.chapter_pages WHERE chapter_id = {chapterId.Value} ORDER BY id FOR SHARE")
                .AsTracking()
                .ToListAsync(token)
            : await dbContext.ChapterPages
                .FromSqlInterpolated($"SELECT p.* FROM catalog.chapter_pages p JOIN catalog.chapters c ON c.id = p.chapter_id WHERE c.comic_id = {comicId} ORDER BY p.id FOR SHARE OF p")
                .AsTracking()
                .ToListAsync(token);

        var validChapterIds = chapters
            .Where(chapter => chapter.PublicationStatus == ChapterPublicationStatuses.Published)
            .Select(chapter => chapter.Id)
            .ToHashSet();
        return pages.Any(page =>
            validChapterIds.Contains(page.ChapterId) &&
            page.AssetStatus == ChapterPageAssetStatuses.Ready &&
            page.Width > 0 &&
            page.Height > 0);
    }

    private async Task<CommentPage> PageAsync(IQueryable<Comment> query, int page, int pageSize, bool ascending, CancellationToken token)
    {
        var total = await query.CountAsync(token);
        query = ascending
            ? query.OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id)
            : query.OrderByDescending(comment => comment.CreatedAt).ThenByDescending(comment => comment.Id);
        var items = await (from comment in query.Skip((page - 1) * pageSize).Take(pageSize)
                           join user in dbContext.Users.AsNoTracking() on comment.UserId equals user.Id
                           select new CommentItem(comment.Id, comment.UserId, user.DisplayName, user.AvatarUrl,
                               comment.ComicId, comment.ChapterId, comment.ParentId, comment.Body, comment.Status,
                               comment.CreatedAt, comment.UpdatedAt, comment.Version,
                                dbContext.Comments.Count(reply => reply.ParentId == comment.Id && reply.Status == CommentStatuses.Visible),
                                comment.ComicId.HasValue
                                    ? dbContext.Comics.Where(comic => comic.Id == comment.ComicId.Value).Select(comic => comic.Title).Single()
                                    : (from chapter in dbContext.Chapters
                                       join comic in dbContext.Comics on chapter.ComicId equals comic.Id
                                       where chapter.Id == comment.ChapterId!.Value
                                       select comic.Title + " · Chương " + chapter.ChapterLabel).Single(),
                                comment.ComicId.HasValue
                                    ? dbContext.Comics.Where(comic => comic.Id == comment.ComicId.Value).Select(comic => comic.Slug).Single()
                                    : null,
                                comment.ParentId.HasValue
                                    ? dbContext.Comments.Where(parent => parent.Id == comment.ParentId.Value).Select(parent => parent.Body).Single()
                                    : null,
                                comment.ParentId.HasValue
                                    ? (from parent in dbContext.Comments
                                       join parentUser in dbContext.Users on parent.UserId equals parentUser.Id
                                       where parent.Id == comment.ParentId.Value
                                       select parentUser.DisplayName).Single()
                                    : null))
            .ToListAsync(token);
        return new(items, page, pageSize, total, total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));
    }

    private async Task<CommentItem> ItemAsync(Guid id, CancellationToken token) =>
        await (from comment in dbContext.Comments.AsNoTracking()
               join user in dbContext.Users.AsNoTracking() on comment.UserId equals user.Id
               where comment.Id == id
               select new CommentItem(comment.Id, comment.UserId, user.DisplayName, user.AvatarUrl,
                   comment.ComicId, comment.ChapterId, comment.ParentId, comment.Body, comment.Status,
                   comment.CreatedAt, comment.UpdatedAt, comment.Version,
                    dbContext.Comments.Count(reply => reply.ParentId == comment.Id && reply.Status == CommentStatuses.Visible)))
            .SingleAsync(token);

    private static CommentResult RuleFailure(CommentRuleViolationException exception) => new(
        exception.Violation == CommentRuleViolation.ConcurrencyConflict ? CommentOutcome.ConcurrencyConflict : CommentOutcome.InvalidState);
}
