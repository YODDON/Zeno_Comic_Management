namespace ComicManagement.Catalog.Administration;

public sealed record AdminPageItem(Guid Id, int PageOrder, string AssetStatus, int Width, int Height);
public sealed record AdminPageManifest(Guid ChapterId, long Version, IReadOnlyList<AdminPageItem> Pages);
public sealed record AdminPageUpload(string FileName, string ContentType, Stream Content);
public enum AdminPageMutationOutcome { Succeeded, ValidationFailed, NotFound, Conflict, PublicationPreconditionFailed, ProviderFailed }
public sealed record AdminPageMutationResult(AdminPageMutationOutcome Outcome, AdminPageManifest? Manifest = null);

public interface IAdminChapterPageCriticalSectionObserver
{
    Task AfterChapterLockedAsync(Guid chapterId, CancellationToken token);
    Task AfterManifestChapterReadAsync(Guid chapterId, CancellationToken token) => Task.CompletedTask;
}

public sealed class NoOpAdminChapterPageCriticalSectionObserver : IAdminChapterPageCriticalSectionObserver
{
    public Task AfterChapterLockedAsync(Guid chapterId, CancellationToken token) => Task.CompletedTask;
}

public interface IAdminChapterPageService
{
    Task<AdminPageManifest?> GetAsync(Guid chapterId, CancellationToken token);
    Task<string?> GetAssetKeyAsync(Guid chapterId, Guid pageId, CancellationToken token);
    Task<AdminPageMutationResult> AppendAsync(Guid chapterId, long expectedVersion,
        IReadOnlyList<AdminPageUpload> files, Guid actorId, string correlationId, CancellationToken token);
    Task<AdminPageMutationResult> ReorderAsync(Guid chapterId, long expectedVersion,
        IReadOnlyList<Guid> pageIds, Guid actorId, string correlationId, CancellationToken token);
    Task<AdminPageMutationResult> RemoveAsync(Guid chapterId, Guid pageId, long expectedVersion,
        Guid actorId, string correlationId, CancellationToken token);
    Task<AdminPageMutationResult> ReplaceAsync(Guid chapterId, Guid pageId, long expectedVersion,
        AdminPageUpload file, Guid actorId, string correlationId, CancellationToken token);
}
