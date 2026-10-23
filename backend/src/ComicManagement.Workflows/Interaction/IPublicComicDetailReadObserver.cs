namespace ComicManagement.Workflows.Interaction;

public interface IPublicComicDetailReadObserver
{
    Task AfterCatalogReadAsync(CancellationToken cancellationToken);
}

public sealed class NoOpPublicComicDetailReadObserver : IPublicComicDetailReadObserver
{
    public Task AfterCatalogReadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
