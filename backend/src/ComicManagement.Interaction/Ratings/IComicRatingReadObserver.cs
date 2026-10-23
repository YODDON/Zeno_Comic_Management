namespace ComicManagement.Interaction.Ratings;

public interface IComicRatingReadObserver
{
    Task AfterPublicVisibilityReadAsync(CancellationToken cancellationToken);
}

public sealed class NoOpComicRatingReadObserver : IComicRatingReadObserver
{
    public Task AfterPublicVisibilityReadAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
