using ComicManagement.Catalog.Browsing;
using ComicManagement.Interaction.Ratings;
using ComicManagement.Shared.Persistence;

namespace ComicManagement.Workflows.Interaction;

public sealed record PublicComicDetailWorkflowResult(
    PublicCatalogOutcome Outcome,
    PublicComicDetail? Detail = null,
    ComicRatingSummary? Rating = null);

public sealed class PublicComicDetailWorkflow(
    IConsistentReadExecutor consistentReadExecutor,
    PublicCatalogHandler catalogHandler,
    ComicRatingHandler ratingHandler,
    IPublicComicDetailReadObserver readObserver)
{
    public Task<PublicComicDetailWorkflowResult> GetAsync(
        string slug,
        Guid? userId,
        CancellationToken cancellationToken = default) =>
        consistentReadExecutor.ExecuteAsync(async transactionCancellationToken =>
        {
            var catalog = await catalogHandler.GetComicAsync(
                slug,
                transactionCancellationToken);
            if (catalog.Outcome != PublicCatalogOutcome.Succeeded || catalog.Detail is null)
            {
                return new PublicComicDetailWorkflowResult(catalog.Outcome);
            }

            await readObserver.AfterCatalogReadAsync(transactionCancellationToken);

            var rating = await ratingHandler.GetAsync(
                catalog.Detail.Id,
                userId,
                transactionCancellationToken);
            if (rating.Outcome != ComicRatingOutcome.Succeeded || rating.Summary is null)
            {
                return new PublicComicDetailWorkflowResult(PublicCatalogOutcome.NotFound);
            }

            return new PublicComicDetailWorkflowResult(
                PublicCatalogOutcome.Succeeded,
                catalog.Detail,
                rating.Summary);
        }, cancellationToken);
}
