namespace ComicManagement.Interaction.Ratings;

public enum ComicRatingOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    ComicUnavailable = 2,
}

public sealed record ComicRatingSummary(
    decimal AverageStars,
    int RatingCount,
    short? MyRating);

public sealed record ComicRatingResult(
    ComicRatingOutcome Outcome,
    ComicRatingSummary? Summary = null);

public interface IComicRatingStore
{
    Task<ComicRatingResult> GetAsync(
        Guid comicId,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<ComicRatingResult> UpsertAsync(
        Guid comicId,
        Guid userId,
        short stars,
        CancellationToken cancellationToken = default);

    Task<ComicRatingResult> DeleteAsync(
        Guid comicId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class ComicRatingHandler(IComicRatingStore store)
{
    public Task<ComicRatingResult> GetAsync(
        Guid comicId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (comicId == Guid.Empty || userId == Guid.Empty)
        {
            return Task.FromResult(new ComicRatingResult(ComicRatingOutcome.ValidationFailed));
        }

        return store.GetAsync(comicId, userId, cancellationToken);
    }

    public Task<ComicRatingResult> UpsertAsync(
        Guid comicId,
        Guid userId,
        int stars,
        CancellationToken cancellationToken = default)
    {
        if (comicId == Guid.Empty || userId == Guid.Empty || stars is < 1 or > 5)
        {
            return Task.FromResult(new ComicRatingResult(ComicRatingOutcome.ValidationFailed));
        }

        return store.UpsertAsync(comicId, userId, checked((short)stars), cancellationToken);
    }

    public Task<ComicRatingResult> DeleteAsync(
        Guid comicId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (comicId == Guid.Empty || userId == Guid.Empty)
        {
            return Task.FromResult(new ComicRatingResult(ComicRatingOutcome.ValidationFailed));
        }

        return store.DeleteAsync(comicId, userId, cancellationToken);
    }
}
