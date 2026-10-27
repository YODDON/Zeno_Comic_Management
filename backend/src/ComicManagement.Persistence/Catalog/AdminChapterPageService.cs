using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog;
using ComicManagement.Catalog.Administration;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Media;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ComicManagement.Persistence;

internal sealed class AdminChapterPageService(
    AppDbContext db,
    IChapterPageAssetStore assets,
    IConsistentReadExecutor consistentRead,
    IAdminChapterPageCriticalSectionObserver observer,
    ILogger<AdminChapterPageService> logger) : IAdminChapterPageService
{
    public Task<AdminPageManifest?> GetAsync(Guid chapterId, CancellationToken token) =>
        consistentRead.ExecuteAsync<AdminPageManifest?>(async cancellationToken =>
    {
        var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == chapterId, token);
        if (chapter is null) return null;
        await observer.AfterManifestChapterReadAsync(chapterId, token);
        var pages = await db.ChapterPages.AsNoTracking().Where(x => x.ChapterId == chapterId)
            .OrderBy(x => x.PageOrder).ThenBy(x => x.Id).ToListAsync(token);
        return Map(chapter, pages);
    }, token);

    public Task<string?> GetAssetKeyAsync(Guid chapterId, Guid pageId, CancellationToken token) =>
        db.ChapterPages.AsNoTracking()
            .Where(x => x.ChapterId == chapterId && x.Id == pageId && x.AssetStatus == ChapterPageAssetStatuses.Ready)
            .Select(x => x.CloudinaryPublicId).SingleOrDefaultAsync(token);

    public async Task<AdminPageMutationResult> AppendAsync(
        Guid chapterId, long expectedVersion, IReadOnlyList<AdminPageUpload> files,
        Guid actorId, string correlationId, CancellationToken token)
    {
        if (!ValidCommand(chapterId, expectedVersion, actorId, correlationId) || files.Count is < 1 or > 100)
            return Result(AdminPageMutationOutcome.ValidationFailed);

        var uploaded = new List<UploadedPage>(files.Count);
        var committed = false;
        try
        {
            foreach (var file in files)
                uploaded.Add(await Upload(chapterId, file, token));
            var result = await PersistAppend(chapterId, expectedVersion, uploaded, actorId, correlationId, token);
            committed = result.Outcome == AdminPageMutationOutcome.Succeeded;
            return result;
        }
        catch (ArgumentException)
        {
            return Result(AdminPageMutationOutcome.ValidationFailed);
        }
        catch (ChapterPageAssetException)
        {
            return Result(AdminPageMutationOutcome.ProviderFailed);
        }
        finally
        {
            if (!committed)
                await Compensate(uploaded, chapterId, correlationId);
        }
    }

    public async Task<AdminPageMutationResult> ReorderAsync(
        Guid chapterId, long expectedVersion, IReadOnlyList<Guid> pageIds,
        Guid actorId, string correlationId, CancellationToken token)
    {
        if (!ValidCommand(chapterId, expectedVersion, actorId, correlationId) || pageIds.Count == 0 ||
            pageIds.Any(x => x == Guid.Empty) || pageIds.Distinct().Count() != pageIds.Count)
            return Result(AdminPageMutationOutcome.ValidationFailed);

        await using var tx = await db.Database.BeginTransactionAsync(token);
        var chapter = await LockChapter(chapterId, token);
        if (chapter is null) return Result(AdminPageMutationOutcome.NotFound);
        await observer.AfterChapterLockedAsync(chapterId, token);
        var pages = await LockPages(chapterId, token);
        if (pages.Count != pageIds.Count || pages.Select(x => x.Id).Except(pageIds).Any())
            return Result(AdminPageMutationOutcome.ValidationFailed);
        var now = await ServerNow(token);
        try { chapter.RecordPageMutation(pages.Any(x => x.IsValid), expectedVersion, now); }
        catch (CatalogRuleViolationException ex) { return RuleResult(ex); }

        // Move persisted values to a disjoint range first so the unique
        // (chapter,page_order) constraint remains valid for any permutation.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.chapter_pages SET page_order = page_order + 1000000 WHERE chapter_id = {chapterId}", token);
        var byId = pages.ToDictionary(x => x.Id);
        var clauses = new List<string>(pageIds.Count);
        var parameters = new List<object>(pageIds.Count + 1);
        for (var index = 0; index < pageIds.Count; index++)
        {
            var page = byId[pageIds[index]];
            var parameterName = $"page{index}";
            clauses.Add($"WHEN @{parameterName} THEN {index + 1}");
            parameters.Add(new NpgsqlParameter(parameterName, page.Id));
            page.ChangeOrder(index + 1);
            var property = db.Entry(page).Property(x => x.PageOrder);
            property.OriginalValue = index + 1;
            property.IsModified = false;
        }
        parameters.Add(new NpgsqlParameter("chapterId", chapterId));
        var finalOrderSql = $"UPDATE catalog.chapter_pages SET page_order = CASE id {string.Join(' ', clauses)} END WHERE chapter_id = @chapterId";
        await db.Database.ExecuteSqlRawAsync(finalOrderSql, parameters, token);
        AddAudit(actorId, "Catalog.ChapterPagesReordered", chapterId, correlationId, now);
        try
        {
            await db.SaveChangesAsync(token);
            var manifest = Map(chapter, pages);
            await tx.CommitAsync(token);
            return Result(AdminPageMutationOutcome.Succeeded, manifest);
        }
        catch (DbUpdateException ex) when (IsConflict(ex)) { return Result(AdminPageMutationOutcome.Conflict); }
    }

    public async Task<AdminPageMutationResult> RemoveAsync(
        Guid chapterId, Guid pageId, long expectedVersion, Guid actorId,
        string correlationId, CancellationToken token)
    {
        if (!ValidCommand(chapterId, expectedVersion, actorId, correlationId) || pageId == Guid.Empty)
            return Result(AdminPageMutationOutcome.ValidationFailed);
        string? oldAsset = null;
        AdminPageMutationResult result;
        await using (var tx = await db.Database.BeginTransactionAsync(token))
        {
            var chapter = await LockChapter(chapterId, token);
            if (chapter is null) return Result(AdminPageMutationOutcome.NotFound);
            await observer.AfterChapterLockedAsync(chapterId, token);
            var pages = await LockPages(chapterId, token);
            var page = pages.SingleOrDefault(x => x.Id == pageId);
            if (page is null) return Result(AdminPageMutationOutcome.NotFound);
            var now = await ServerNow(token);
            try { chapter.RecordPageMutation(pages.Any(x => x.Id != pageId && x.IsValid), expectedVersion, now); }
            catch (CatalogRuleViolationException ex) { return RuleResult(ex); }
            oldAsset = page.CloudinaryPublicId;
            db.ChapterPages.Remove(page);
            AddAudit(actorId, "Catalog.ChapterPageRemoved", pageId, correlationId, now);
            try
            {
                await db.SaveChangesAsync(token);
                result = Result(AdminPageMutationOutcome.Succeeded, Map(chapter, pages.Where(x => x.Id != pageId)));
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException ex) when (IsConflict(ex)) { return Result(AdminPageMutationOutcome.Conflict); }
        }
        await DeleteCommittedAsset(oldAsset, chapterId, pageId, correlationId);
        return result;
    }

    public async Task<AdminPageMutationResult> ReplaceAsync(
        Guid chapterId, Guid pageId, long expectedVersion, AdminPageUpload file,
        Guid actorId, string correlationId, CancellationToken token)
    {
        if (!ValidCommand(chapterId, expectedVersion, actorId, correlationId) || pageId == Guid.Empty)
            return Result(AdminPageMutationOutcome.ValidationFailed);
        UploadedPage replacement;
        try { replacement = await Upload(chapterId, file, token); }
        catch (ArgumentException) { return Result(AdminPageMutationOutcome.ValidationFailed); }
        catch (ChapterPageAssetException) { return Result(AdminPageMutationOutcome.ProviderFailed); }

        string? oldAsset = null;
        AdminPageMutationResult result;
        try
        {
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                var chapter = await LockChapter(chapterId, token);
                if (chapter is null) { await Compensate([replacement], chapterId, correlationId); return Result(AdminPageMutationOutcome.NotFound); }
                await observer.AfterChapterLockedAsync(chapterId, token);
                var pages = await LockPages(chapterId, token);
                var oldPage = pages.SingleOrDefault(x => x.Id == pageId);
                if (oldPage is null) { await Compensate([replacement], chapterId, correlationId); return Result(AdminPageMutationOutcome.NotFound); }
                var now = await ServerNow(token);
                try { chapter.RecordPageMutation(true, expectedVersion, now); }
                catch (CatalogRuleViolationException ex) { await Compensate([replacement], chapterId, correlationId); return RuleResult(ex); }
                oldAsset = oldPage.CloudinaryPublicId;
                var newPage = ReadyEntity(replacement, oldPage.PageOrder, now);
                try
                {
                    db.ChapterPages.Remove(oldPage);
                    await db.SaveChangesAsync(token);
                    db.ChapterPages.Add(newPage);
                    AddAudit(actorId, "Catalog.ChapterPageReplaced", pageId, correlationId, now);
                    await db.SaveChangesAsync(token);
                    var manifestPages = pages.Where(x => x.Id != pageId).Append(newPage);
                    result = Result(AdminPageMutationOutcome.Succeeded, Map(chapter, manifestPages));
                    await tx.CommitAsync(token);
                }
                catch (DbUpdateException ex) when (IsConflict(ex))
                {
                    await Compensate([replacement], chapterId, correlationId);
                    return Result(AdminPageMutationOutcome.Conflict);
                }
            }
        }
        catch
        {
            await Compensate([replacement], chapterId, correlationId);
            throw;
        }
        await DeleteCommittedAsset(oldAsset, chapterId, pageId, correlationId);
        return result;
    }

    private async Task<AdminPageMutationResult> PersistAppend(Guid chapterId, long expectedVersion,
        IReadOnlyList<UploadedPage> uploaded, Guid actorId, string correlationId, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var chapter = await LockChapter(chapterId, token);
        if (chapter is null) return Result(AdminPageMutationOutcome.NotFound);
        await observer.AfterChapterLockedAsync(chapterId, token);
        var pages = await LockPages(chapterId, token);
        var now = await ServerNow(token);
        try { chapter.RecordPageMutation(true, expectedVersion, now); }
        catch (CatalogRuleViolationException ex) { return RuleResult(ex); }
        var nextOrder = pages.Count == 0 ? 1 : pages.Max(x => x.PageOrder) + 1;
        var additions = uploaded.Select((x, index) => ReadyEntity(x, nextOrder + index, now)).ToArray();
        db.ChapterPages.AddRange(additions);
        AddAudit(actorId, "Catalog.ChapterPagesAppended", chapterId, correlationId, now);
        try
        {
            await db.SaveChangesAsync(token);
            var manifest = Map(chapter, pages.Concat(additions));
            await tx.CommitAsync(token);
            return Result(AdminPageMutationOutcome.Succeeded, manifest);
        }
        catch (DbUpdateException ex) when (IsConflict(ex)) { return Result(AdminPageMutationOutcome.Conflict); }
    }

    private async Task<UploadedPage> Upload(Guid chapterId, AdminPageUpload file, CancellationToken token)
    {
        var upload = new ChapterPageAssetUpload(chapterId, Guid.NewGuid(), file.FileName, file.ContentType, file.Content);
        assets.ValidateUpload(upload);
        var fingerprint = await upload.ComputeFingerprintAsync(token);
        var asset = await assets.UploadAsync(upload, token);
        return new(chapterId, upload.PageId, asset.PublicId, asset.Width, asset.Height, fingerprint);
    }

    private static ChapterPage ReadyEntity(UploadedPage upload, int order, DateTimeOffset now)
    {
        var page = ChapterPage.CreatePending(upload.Id, upload.ChapterId, order, upload.PublicId, upload.Fingerprint, now);
        page.MarkReady(upload.Width, upload.Height);
        return page;
    }

    private async Task<Chapter?> LockChapter(Guid chapterId, CancellationToken token)
    {
        var comicId = await db.Chapters.AsNoTracking().Where(x => x.Id == chapterId)
            .Select(x => (Guid?)x.ComicId).SingleOrDefaultAsync(token);
        if (comicId is null) return null;
        _ = await db.Comics.FromSqlInterpolated(
                $"SELECT * FROM catalog.comics WHERE id = {comicId.Value} FOR UPDATE")
            .SingleAsync(token);
        return await db.Chapters.FromSqlInterpolated(
                $"SELECT * FROM catalog.chapters WHERE id = {chapterId} FOR UPDATE")
            .SingleOrDefaultAsync(token);
    }

    private async Task<List<ChapterPage>> LockPages(Guid chapterId, CancellationToken token) =>
        await db.ChapterPages.FromSqlInterpolated(
                $"SELECT * FROM catalog.chapter_pages WHERE chapter_id = {chapterId} ORDER BY page_order, id FOR UPDATE")
            .ToListAsync(token);

    private async Task Compensate(
        IEnumerable<UploadedPage> uploaded,
        Guid chapterId,
        string correlationId)
    {
        foreach (var page in uploaded)
        {
            try { await assets.DeleteAsync(page.PublicId, CancellationToken.None); }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Cloudinary compensation failed. Provider={Provider} PublicId={PublicId} ChapterId={ChapterId} PageId={PageId} CorrelationId={CorrelationId}",
                    "Cloudinary", page.PublicId, chapterId, page.Id, correlationId);
            }
        }
    }

    private async Task DeleteCommittedAsset(
        string? publicId,
        Guid chapterId,
        Guid pageId,
        string correlationId)
    {
        if (publicId is null) return;
        try { await assets.DeleteAsync(publicId, CancellationToken.None); }
        catch (Exception ex)
        {
            // The DB mutation is authoritative after commit. Keep the orphan key in
            // provider logs for the operations cleanup runbook; never roll the DB back.
            logger.LogError(ex,
                "Committed Cloudinary asset cleanup failed. Provider={Provider} PublicId={PublicId} ChapterId={ChapterId} PageId={PageId} CorrelationId={CorrelationId}",
                "Cloudinary", publicId, chapterId, pageId, correlationId);
        }
    }

    private void AddAudit(Guid actorId, string action, Guid targetId, string correlationId, DateTimeOffset now) =>
        db.AuditLogs.Add(AuditLog.Create(Guid.NewGuid(), actorId, action, "ChapterPage", targetId,
            correlationId, now, "{}"));

    private static AdminPageManifest Map(Chapter chapter, IEnumerable<ChapterPage> pages) =>
        new(chapter.Id, chapter.Version, pages.OrderBy(x => x.PageOrder).ThenBy(x => x.Id)
            .Select(x => new AdminPageItem(x.Id, x.PageOrder, x.AssetStatus, x.Width, x.Height)).ToArray());
    private Task<DateTimeOffset> ServerNow(CancellationToken token) => db.Database
        .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
    private static bool ValidCommand(Guid chapterId, long expectedVersion, Guid actorId, string correlationId) =>
        chapterId != Guid.Empty && expectedVersion > 0 && actorId != Guid.Empty && !string.IsNullOrWhiteSpace(correlationId);
    private static bool IsConflict(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: "23505" or "40001" };
    private static AdminPageMutationResult RuleResult(CatalogRuleViolationException ex) => Result(
        ex.Violation == CatalogRuleViolation.ConcurrencyConflict
            ? AdminPageMutationOutcome.Conflict
            : AdminPageMutationOutcome.PublicationPreconditionFailed);
    private static AdminPageMutationResult Result(AdminPageMutationOutcome outcome, AdminPageManifest? manifest = null) =>
        new(outcome, manifest);
    private sealed record UploadedPage(Guid ChapterId, Guid Id, string PublicId, int Width, int Height, string Fingerprint);
}
