using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Media;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ChapterPageUploadStore(AppDbContext dbContext) : IChapterPageUploadStore
{
    public async Task<ChapterPage> GetOrCreatePendingAsync(
        ChapterPage pendingPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pendingPage);
        if (pendingPage.AssetStatus != ChapterPageAssetStatuses.Pending)
        {
            throw new ArgumentException("A pending chapter page is required.", nameof(pendingPage));
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.chapter_pages
                (id, chapter_id, page_order, cloudinary_public_id, asset_status,
                 upload_fingerprint, upload_created_at, width, height)
            VALUES
                ({pendingPage.Id}, {pendingPage.ChapterId}, {pendingPage.PageOrder},
                 {pendingPage.CloudinaryPublicId}, {ChapterPageAssetStatuses.Pending},
                 {pendingPage.UploadFingerprint}, {pendingPage.UploadCreatedAt}, 0, 0)
            ON CONFLICT DO NOTHING;
            """,
            cancellationToken);

        var persistedPage = await FindAsync(pendingPage.Id, cancellationToken)
            ?? throw new ChapterPageUploadConflictException(
                "The requested page order or provider asset is already assigned.");

        if (persistedPage.ChapterId != pendingPage.ChapterId ||
            persistedPage.PageOrder != pendingPage.PageOrder ||
            !string.Equals(
                persistedPage.CloudinaryPublicId,
                pendingPage.CloudinaryPublicId,
                StringComparison.Ordinal) ||
            !string.Equals(
                persistedPage.UploadFingerprint,
                pendingPage.UploadFingerprint,
                StringComparison.Ordinal))
        {
            throw new ChapterPageUploadConflictException(
                "The page ID was already used for a different upload request.");
        }

        return persistedPage;
    }

    public async Task<ChapterPage> MarkReadyAsync(
        Guid pageId,
        ChapterPageAsset asset,
        CancellationToken cancellationToken = default)
    {
        if (pageId == Guid.Empty)
        {
            throw new ArgumentException("Page ID is required.", nameof(pageId));
        }

        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Width <= 0 || asset.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(asset));
        }

        var affectedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.chapter_pages
            SET asset_status = {ChapterPageAssetStatuses.Ready},
                width = {asset.Width},
                height = {asset.Height}
            WHERE id = {pageId}
              AND cloudinary_public_id = {asset.PublicId}
              AND (
                    asset_status = {ChapterPageAssetStatuses.Pending}
                    OR (
                        asset_status = {ChapterPageAssetStatuses.Ready}
                        AND width = {asset.Width}
                        AND height = {asset.Height}
                    )
                  );
            """,
            cancellationToken);

        if (affectedRows == 0)
        {
            throw new ChapterPageUploadConflictException(
                "The persisted page does not match the uploaded asset.");
        }

        return await FindAsync(pageId, cancellationToken)
            ?? throw new ChapterPageUploadConflictException("The persisted page no longer exists.");
    }

    private Task<ChapterPage?> FindAsync(Guid pageId, CancellationToken cancellationToken) =>
        dbContext.ChapterPages
            .AsNoTracking()
            .SingleOrDefaultAsync(page => page.Id == pageId, cancellationToken);
}
