using ComicManagement.Interaction.Ratings;
using Xunit;

namespace ComicManagement.Interaction.UnitTests;

public sealed class ComicRatingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Rating_accepts_only_one_to_five_stars_and_utc_timestamps()
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var rating = ComicRating.Create(userId, comicId, 1, Now);

        rating.Update(5, Now.AddMinutes(1));

        Assert.Equal((short)5, rating.Stars);
        Assert.Equal(Now.AddMinutes(1), rating.UpdatedAt);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComicRating.Create(userId, comicId, 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => rating.Update(6, Now.AddMinutes(2)));
        Assert.Throws<ArgumentException>(
            () => ComicRating.Create(userId, comicId, 3, Now.ToOffset(TimeSpan.FromHours(7))));
    }

    [Fact]
    public async Task Handler_rejects_invalid_input_before_calling_store()
    {
        var store = new RecordingStore();
        var handler = new ComicRatingHandler(store);

        Assert.Equal(
            ComicRatingOutcome.ValidationFailed,
            (await handler.UpsertAsync(Guid.NewGuid(), Guid.NewGuid(), 0)).Outcome);
        Assert.Equal(
            ComicRatingOutcome.ValidationFailed,
            (await handler.GetAsync(Guid.Empty, null)).Outcome);
        Assert.Equal(0, store.CallCount);
    }

    private sealed class RecordingStore : IComicRatingStore
    {
        public int CallCount { get; private set; }

        public Task<ComicRatingResult> GetAsync(
            Guid comicId,
            Guid? userId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Success();
        }

        public Task<ComicRatingResult> UpsertAsync(
            Guid comicId,
            Guid userId,
            short stars,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Success();
        }

        public Task<ComicRatingResult> DeleteAsync(
            Guid comicId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Success();
        }

        private static Task<ComicRatingResult> Success() => Task.FromResult(
            new ComicRatingResult(
                ComicRatingOutcome.Succeeded,
                new ComicRatingSummary(0, 0, null)));
    }
}
